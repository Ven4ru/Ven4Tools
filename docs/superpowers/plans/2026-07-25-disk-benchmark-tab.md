# План реализации вкладки «Бенчмарк»

> **Для исполнителя:** план выполняется по задачам, шаги отмечаются чекбоксами.
> Спека: `docs/superpowers/specs/2026-07-25-disk-benchmark-tab-design.md`.

**Цель:** встроить в клиент Ven4Tools измеритель скорости накопителей с определением типа
подключения и сопоставлением результата с потолком интерфейса, полностью офлайн.

**Архитектура:** пять сервисов в `Ven4Tools/Services/DiskBenchmark/` с узкими границами
(инвентаризация, линия PCIe, движок замеров, предупреждения, отчёт) плюс вкладка из пяти
partial-файлов по образцу `DiagnosticsTab`. Движок ввода-вывода обходит кэш операционной
системы через `FILE_FLAG_NO_BUFFERING` без P/Invoke и без `unsafe`.

**Стек:** .NET 8, WPF, `System.Management` (WMI), xunit в `tests/Ven4Tools.Tests`.

## Общие ограничения

- Весь текст в коде, комментариях и интерфейсе — только на русском.
- Нигде не упоминать используемые инструменты разработки и их названия.
- Идентификаторы C# — на английском, как во всей кодовой базе (`RebootCategory.Bsod`).
- Бэкап изменяемого существующего файла в `_backups\ГГГГММДД_ЧЧММСС\` делается ДО правки.
- `dotnet build` — 0 ошибок, 0 предупреждений.
- **Коммитов в этой сессии нет.** Пользователь их не запрашивал, поэтому вместо шага
  «Commit» каждая задача завершается шагом проверки сборки и тестов. Это осознанное
  отклонение от стандартного вида плана.
- Не трогать `Secrets.cs`, `_release/`, `_backups/`, `bin/`, `obj/`.
- Не добавлять новые классы в список `<Include>` покрытия в `Ven4Tools.Tests.csproj` —
  это порог покрытия 80%, менять его в рамках фичи не требуется.
- Никакой сетевой активности: ни одного `HttpClient`, ни одного URL.
- Никакой прямой записи в устройство или секторы — только временный файл на томе.

---

### Задача 1: Модели

**Файлы:**
- Создать: `Ven4Tools/Models/DiskBenchmarkModels.cs`

**Интерфейсы:**
- Отдаёт: `DiskBusKind`, `DiskMediaKind`, `PciLinkInfo`, `BenchmarkVolumeInfo`,
  `PhysicalDiskInfo`, `BenchmarkPattern`, `BenchmarkOperation`, `BenchmarkMeasurement`,
  `BenchmarkProfile`, `BenchmarkRunResult`, `BenchmarkProgress` в `Ven4Tools.Models`.

- [ ] **Шаг 1: Создать файл моделей**

Ключевые решения, которые нельзя менять в последующих задачах:

```csharp
public sealed class PciLinkInfo
{
    public static readonly PciLinkInfo Unknown = new PciLinkInfo();
    public int Generation { get; init; }   // 0 — не определено
    public int Width { get; init; }        // 0 — не определено
    public bool IsKnown => Generation >= 1 && Generation <= 5 && Width > 0;

    /// <summary>Полезная пропускная способность одной линии, МБ/с (десятичные).</summary>
    private static double LaneThroughput(int generation) => generation switch
    {
        1 => 250, 2 => 500, 3 => 985, 4 => 1969, 5 => 3938, _ => 0
    };

    public double CeilingMegabytesPerSecond => IsKnown ? LaneThroughput(Generation) * Width : 0;
}
```

`BenchmarkMeasurement` хранит `PatternName`, `Operation`, `MegabytesPerSecond`,
`OperationsPerSecond`, `AverageLatencyMicroseconds`.

`BenchmarkRunResult` хранит `Disk`, `VolumeLetter`, `Profile`, `Passes`, `FileSizeBytes`,
`StartedAt`, `Duration`, `Cancelled`, `Measurements` (список), `Warnings` (список строк).

- [ ] **Шаг 2: Проверить сборку**

Выполнить: `dotnet build Ven4Tools/Ven4Tools.csproj -v q`
Ожидается: `0 Error(s)`, `0 Warning(s)`.

---

### Задача 2: Расчёт потолка интерфейса (TDD)

**Файлы:**
- Тест: `tests/Ven4Tools.Tests/PciLinkInfoTests.cs`

**Интерфейсы:**
- Потребляет: `PciLinkInfo` из задачи 1.

- [ ] **Шаг 1: Написать падающий тест**

```csharp
[Fact]
public void ЛинияPcie4x4_ДаётПотолокОколо7900МБс()
{
    var link = new PciLinkInfo { Generation = 4, Width = 4 };
    Assert.True(link.IsKnown);
    Assert.Equal(7876, link.CeilingMegabytesPerSecond, 0);
}

[Theory]
[InlineData(0, 4)]
[InlineData(4, 0)]
[InlineData(9, 4)]
public void НеполныеИлиНеизвестныеДанные_ПотолокНеСчитается(int generation, int width)
{
    var link = new PciLinkInfo { Generation = generation, Width = width };
    Assert.False(link.IsKnown);
    Assert.Equal(0, link.CeilingMegabytesPerSecond);
}
```

- [ ] **Шаг 2: Прогнать тесты**

Выполнить: `dotnet test tests/Ven4Tools.Tests --filter PciLinkInfoTests`
Ожидается: PASS (модель уже реализована в задаче 1; тест фиксирует контракт честности —
при неизвестных данных потолок равен нулю и наверх не выводится).

---

### Задача 3: Движок замеров

**Файлы:**
- Создать: `Ven4Tools/Services/DiskBenchmark/DiskBenchmarkEngine.cs`

**Интерфейсы:**
- Потребляет: модели из задачи 1.
- Отдаёт:
  - `DiskBenchmarkEngine.Patterns` — `IReadOnlyList<BenchmarkPattern>` из четырёх паттернов;
  - `static int PassesForProfile(BenchmarkProfile profile)`;
  - `static Task<BenchmarkRunResult> RunAsync(PhysicalDiskInfo disk, BenchmarkVolumeInfo volume,
    BenchmarkProfile profile, long fileSizeBytes, IProgress<BenchmarkProgress>? progress,
    CancellationToken ct)`;
  - `static string TempFileName` — `"Ven4Tools_benchmark.tmp"`;
  - `static void CleanupOrphanedFiles()`.

- [ ] **Шаг 1: Константы и паттерны**

```csharp
/// <summary>FILE_FLAG_NO_BUFFERING — полный обход кэша файловой системы.</summary>
private const FileOptions NoBuffering = (FileOptions)0x20000000;

/// <summary>Кратно и 512-байтному, и 4096-байтному физическому сектору.</summary>
private const int Alignment = 4096;

private static readonly TimeSpan PassDuration = TimeSpan.FromSeconds(5);
```

Паттерны: SEQ1M Q8T1 (блок 1 МиБ, очередь 8, потоков 1, последовательный),
SEQ1M Q1T1, RND4K Q32T16 (блок 4 КиБ, очередь 32, потоков 16, случайный), RND4K Q1T1.

- [ ] **Шаг 2: Выделение выровненного буфера**

```csharp
// Обычный массив закрепляется, адрес округляется вверх до границы сектора.
// Пока дескриптор закреплён, сборщик мусора массив не двигает, выравнивание сохраняется.
byte[] raw = new byte[(long)streams * block + Alignment];
var pin = GCHandle.Alloc(raw, GCHandleType.Pinned);
int alignOffset = (int)((Alignment - (pin.AddrOfPinnedObject().ToInt64() % Alignment)) % Alignment);
```

- [ ] **Шаг 3: Подготовка тестового файла**

`SetLength` до нужного размера, затем полная запись псевдослучайными данными блоками по
1 МиБ с восемью незавершёнными операциями. Файл обязан быть записан целиком: чтение
неинициализированных областей разреженного файла возвращается мгновенно и обесценивает
замер. Прогресс подготовки уходит в `IProgress`.

- [ ] **Шаг 4: Один замер**

```csharp
int streams = pattern.QueueDepth * pattern.ThreadCount;
long deadline = Stopwatch.GetTimestamp() + (long)(PassDuration.TotalSeconds * Stopwatch.Frequency);
var tasks = new Task[streams];
for (int i = 0; i < streams; i++) tasks[i] = StreamLoopAsync(i);
await Task.WhenAll(tasks);
```

Каждый поток запросов работает со своим непересекающимся срезом закреплённого буфера и в
цикле ожидает `RandomAccess.ReadAsync` либо `RandomAccess.WriteAsync`. Потоков
операционной системы при этом не создаётся — 512 незавершённых перекрывающихся операций
обслуживает пул. Последовательные паттерны делят файл на равные участки по числу потоков
запросов, случайные выбирают выровненное смещение генератором с фиксированным зерном.

Метрики:

```csharp
double seconds = elapsed.TotalSeconds;
double megabytesPerSecond = totalOperations * (double)block / 1_000_000d / seconds;
double operationsPerSecond = totalOperations / seconds;
// Закон Литтла: средняя задержка = глубина очереди / пропускная способность в операциях.
double averageLatencyMicroseconds = streams / operationsPerSecond * 1_000_000d;
```

- [ ] **Шаг 5: Оркестрация прогона**

Проходы по профилю (1 / 3 / 5), внутри каждого прохода — четыре паттерна, в каждом
чтение и запись. Итоговое значение метрики — лучший проход. Прогресс — доля выполненных
замеров. Удаление файла — в `finally`, включая отмену и исключение.

- [ ] **Шаг 6: Проверить сборку**

Выполнить: `dotnet build Ven4Tools/Ven4Tools.csproj -v q`
Ожидается: `0 Error(s)`, `0 Warning(s)`.

---

### Задача 4: Движок на реальном файле (интеграционная проверка)

**Файлы:**
- Тест: `tests/Ven4Tools.Tests/DiskBenchmarkEngineTests.cs`

**Интерфейсы:**
- Потребляет: `DiskBenchmarkEngine` из задачи 3.

- [ ] **Шаг 1: Тест на выравнивание и на реальный ввод-вывод**

Тест открывает дескриптор с `NoBuffering` во временной папке, пишет и читает выровненный
блок и проверяет, что данные совпали. Это доказывает, что связка «флаг + выравнивание»
рабочая на машине, где идёт сборка, а не только в теории.

```csharp
[Fact]
public async Task НебуферизованныйДескриптор_ПишетИЧитаетВыровненныйБлок()
```

- [ ] **Шаг 2: Прогнать тест**

Выполнить: `dotnet test tests/Ven4Tools.Tests --filter DiskBenchmarkEngineTests`
Ожидается: PASS.

---

### Задача 5: Инвентаризация накопителей и линия PCIe

**Файлы:**
- Создать: `Ven4Tools/Services/DiskBenchmark/DiskInventoryService.cs`
- Создать: `Ven4Tools/Services/DiskBenchmark/PciLinkResolver.cs`

**Интерфейсы:**
- Отдаёт: `DiskInventoryService.GetDisksAsync()` → `Task<List<PhysicalDiskInfo>>`;
  `PciLinkResolver.Resolve(string pnpDeviceId)` → `PciLinkInfo`.

- [ ] **Шаг 1: Инвентаризация**

`MSFT_PhysicalDisk` в `ROOT\Microsoft\Windows\Storage` даёт `DeviceId`, `FriendlyName`,
`Size`, `BusType`, `MediaType`, `SpindleSpeed`. Серийный номер не читается сознательно:
отчёт пользователь копирует и может опубликовать.

Тома привязываются к накопителю через `Win32_DiskDrive` (`Index` совпадает с `DeviceId`),
далее `ASSOCIATORS OF` по `Win32_DiskDriveToDiskPartition` и
`Win32_LogicalDiskToPartition`. Обратные слэши в пути WMI удваиваются.

Если пространство имён хранилища недоступно, работает запасной путь по одному
`Win32_DiskDrive`: шина и тип носителя остаются неизвестными — честно, без догадок.

- [ ] **Шаг 2: Линия PCIe**

Через метод `GetDeviceProperties` класса `Win32_PnPEntity`: сначала `DEVPKEY_Device_Parent`
(`{4340A6C5-93FA-4706-972C-7B648008A5A7} 8`), подъём по родителям до узла `PCI\`, но не
более четырёх уровней; на нём — `{3AB22E31-8264-4B4E-9AF5-A8D2D8E33E62} 9` (поколение) и
`{3AB22E31-8264-4B4E-9AF5-A8D2D8E33E62} 10` (число линий). Любой сбой возвращает
`PciLinkInfo.Unknown`, не роняя список накопителей.

- [ ] **Шаг 3: Проверить на живой системе**

Выполнить одноразовый пробник в scratchpad, который печатает список накопителей, шину,
тип носителя и линию PCIe. Сверить с действительностью. Убедиться, что при недоступности
свойств выводится «неизвестно», а не выдуманное значение.

---

### Задача 6: Предупреждения (TDD)

**Файлы:**
- Создать: `Ven4Tools/Services/DiskBenchmark/BenchmarkWarningService.cs`
- Тест: `tests/Ven4Tools.Tests/BenchmarkWarningServiceTests.cs`

**Интерфейсы:**
- Отдаёт: чистая функция
  `BenchmarkWarningService.Build(bool isSystemVolume, double usedPercent, bool bitLocker, bool removable)`
  → `List<string>`; сбор фактов — `CollectAsync(...)`; проверка места —
  `TryValidateFreeSpace(BenchmarkVolumeInfo volume, long fileSizeBytes, out string error)`.

- [ ] **Шаг 1: Написать падающие тесты**

```csharp
[Fact] public void ЧистыйНесистемныйТом_БезПредупреждений()
[Fact] public void ЗаполненныйБолее90Процентов_ДаётПредупреждение()
[Fact] public void СистемныйТом_ДаётПредупреждение()
[Fact] public void BitLocker_ДаётПредупреждение()
[Fact] public void НедостаточноМеста_БлокируетЗапуск()
```

Блокировка ровно одна — нехватка свободного места (размер файла плюс 1 ГиБ запаса).
Остальное предупреждает, но не запрещает.

- [ ] **Шаг 2: Прогнать, убедиться в падении**

Выполнить: `dotnet test tests/Ven4Tools.Tests --filter BenchmarkWarningServiceTests`
Ожидается: ошибка компиляции — тип не существует.

- [ ] **Шаг 3: Реализовать**

Чистая функция плюс сбор фактов: BitLocker через `Win32_EncryptableVolume` в
`ROOT\CIMV2\Security\MicrosoftVolumeEncryption` (`ProtectionStatus`), системный том — через
`Path.GetPathRoot(Environment.SystemDirectory)`.

- [ ] **Шаг 4: Прогнать тесты**

Ожидается: PASS.

---

### Задача 7: Отчёт (TDD)

**Файлы:**
- Создать: `Ven4Tools/Services/DiskBenchmark/BenchmarkReportBuilder.cs`
- Тест: `tests/Ven4Tools.Tests/BenchmarkReportBuilderTests.cs`

**Интерфейсы:**
- Отдаёт: `BenchmarkReportBuilder.Build(BenchmarkRunResult result)` → `string`;
  `BenchmarkReportBuilder.DescribeLevel(double sequentialReadMbPerSec)` → `string`;
  `BenchmarkReportBuilder.DescribeConnection(PhysicalDiskInfo disk)` → `string`.

- [ ] **Шаг 1: Написать падающие тесты**

```csharp
[Fact]
public void ПриНеизвестнойЛинии_ПотолокИПроцентНеПоказываются()
{
    // Ключевой тест честности: в отчёте не должно быть ни «потолок», ни процента шины,
    // если поколение или ширина линии не определились.
}

[Fact]
public void ПриИзвестнойЛинии_ПоказываетсяПотолокИДоляЕгоИспользования()

[Fact]
public void Уровень_ОписываетсяКакСопоставимость_АНеКакФакт()
```

Форматирование чисел — явно в культуре `ru-RU`, чтобы отчёт не зависел от локали машины и
тесты были детерминированными.

- [ ] **Шаг 2: Прогнать, убедиться в падении**

- [ ] **Шаг 3: Реализовать**

Отчёт содержит: шапку с датой, сведения о накопителе и подключении, параметры прогона,
таблицу из четырёх строк с чтением и записью (МБ/с, операции в секунду, задержка), раздел
предупреждений, раздел выводов. При отмене — явная пометка о неполноте результата.

- [ ] **Шаг 4: Прогнать тесты**

Ожидается: PASS.

---

### Задача 8: Вкладка

**Файлы:**
- Создать: `Ven4Tools/Views/Tabs/BenchmarkTab.xaml`
- Создать: `Ven4Tools/Views/Tabs/BenchmarkTab.xaml.cs`
- Создать: `Ven4Tools/Views/Tabs/BenchmarkTab.Disks.cs`
- Создать: `Ven4Tools/Views/Tabs/BenchmarkTab.Run.cs`
- Создать: `Ven4Tools/Views/Tabs/BenchmarkTab.Report.cs`

- [ ] **Шаг 1: Разметка**

Заголовок страницы «Тест скорости диска». Карточки: выбор накопителя и тома; сведения о
выбранном накопителе; параметры прогона (профиль, размер файла); предупреждения; запуск и
прогресс; таблица результатов; выводы; кнопки отчёта.

Списки — `ComboBox`. Ползунков в разметке нет: подписка `Slider.ValueChanged` через
атрибут XAML в этом проекте приводит к падению при первом открытии вкладки, поэтому
ползунки не используются вовсе.

- [ ] **Шаг 2: Код вкладки**

Конструктор подписывает обработчики и вызывает `InitializeComponent`. Загрузка списка
накопителей и подчистка осиротевших временных файлов — в `Loaded`, однократно, по образцу
`DiagnosticsTab`.

- [ ] **Шаг 3: Запуск и отмена**

Кнопка запуска на время прогона превращается в кнопку остановки, работает
`CancellationTokenSource`. Прогресс показывает этап словами. Отмена — штатный путь:
частичные результаты остаются с пометкой.

- [ ] **Шаг 4: Проверить сборку**

Выполнить: `dotnet build Ven4Tools/Ven4Tools.csproj -v q`
Ожидается: `0 Error(s)`, `0 Warning(s)`.

---

### Задача 9: Регистрация вкладки в главном окне

**Файлы:**
- Изменить: `Ven4Tools/MainWindow.xaml` (раздел «WINDOWS И СИСТЕМА», после «Диагностики»)
- Изменить: `Ven4Tools/MainWindow.xaml.cs` (поле, навигация, массив в `SetActiveButton`)

- [ ] **Шаг 1: Бэкап ДО правки**

Скопировать оба файла в `_backups\ГГГГММДД_ЧЧММСС\` до внесения изменений.

- [ ] **Шаг 2: Кнопка навигации**

```xml
<Button x:Name="btnBenchmarkTab" Style="{StaticResource NavButtonStyle}" Tag="&#xE916;"
        Content="Бенчмарк" Click="NavigateToBenchmark"/>
```

- [ ] **Шаг 3: Навигация**

```csharp
private BenchmarkTab? _benchmarkTab;

private void NavigateToBenchmark(object? sender, RoutedEventArgs? e)
{
    SetActiveButton(btnBenchmarkTab);
    AppLogger.Write("📂 Открыта вкладка: Бенчмарк");
    if (_benchmarkTab == null) _benchmarkTab = new BenchmarkTab();
    MainFrame.Content = (_benchmarkTab);
    UpdateMascot("system");
}
```

Добавить `btnBenchmarkTab` в массив кнопок внутри `SetActiveButton`, иначе подсветка
активной вкладки не будет сбрасываться.

- [ ] **Шаг 4: Проверить сборку**

Выполнить: `dotnet build Ven4Tools/Ven4Tools.csproj -v q`
Ожидается: `0 Error(s)`, `0 Warning(s)`.

---

### Задача 10: Проверка живьём

- [ ] **Шаг 1: Полная сборка решения**

Выполнить: `dotnet build Ven4Tools.sln -v q`
Ожидается: `0 Error(s)`, `0 Warning(s)`.

- [ ] **Шаг 2: Все юнит-тесты**

Выполнить: `dotnet test tests/Ven4Tools.Tests`
Ожидается: ни одного упавшего теста.

- [ ] **Шаг 3: Запустить клиент и открыть вкладку**

Проверить: список накопителей заполнен и соответствует действительности; сведения о шине
верны; при неизвестной линии PCIe выводится «неизвестно» без потолка; прогон профиля
«Быстрый» проходит целиком; отчёт формируется; временный файл после прогона отсутствует.

- [ ] **Шаг 4: Проверить отмену**

Запустить прогон, отменить на середине, убедиться, что временный файл удалён, а
приложение осталось работоспособным.

- [ ] **Шаг 5: Проверить отсутствие утечек в код**

Просмотреть содержимое всех новых файлов: ни паролей, ни токенов, ни IP-адресов, ни
упоминаний инструментов разработки, ни английских комментариев, ни сетевых вызовов.

---

## Самопроверка плана

**Покрытие спеки.** Движок ввода-вывода — задачи 3 и 4. Паттерны и профили — задача 3.
Тестовый файл, его удаление и подчистка осиротевших — задача 3. Инвентаризация, шина,
носитель — задача 5. Линия PCIe и принцип честности — задачи 2, 5, 7. Предупреждения и
единственная жёсткая блокировка — задача 6. Отчёт в буфер и в файл — задачи 7 и 8.
Структура кода — задачи 1, 3, 5, 6, 7, 8. Регистрация вкладки — задача 9. Обработка
ошибок — внутри задач 5, 6, 8. Проверка — задача 10.

**Плейсхолдеры.** Не найдено.

**Согласованность имён.** `PciLinkInfo.IsKnown` и `CeilingMegabytesPerSecond` используются
в задачах 2, 5, 7 в одном написании. `DiskBenchmarkEngine.RunAsync` вызывается из задачи 8
с той же сигнатурой, что объявлена в задаче 3. `BenchmarkReportBuilder.Build` — из задачи 8
с сигнатурой из задачи 7.
