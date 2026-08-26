# BenchmarkTab — миграция на MVVM (одиннадцатая и последняя вкладка)

## Контекст

`BenchmarkTab` (607 строк, 4 code-behind файла: `.xaml.cs` — ядро, `.Disks.cs`, `.Run.cs`, `.Report.cs`) — тест скорости диска: выбор накопителя/тома/профиля/размера файла, живые предупреждения о факторах, влияющих на результат (с защитой от гонки двойного пересчёта), запуск/остановка теста с прогрессом, таблица результатов по 4 паттернам нагрузки (Read/Write), текстовые выводы, экспорт отчёта. Полностью офлайн.

После этой вкладки клиент Ven4Tools полностью переходит на MVVM — это последняя вкладка серии (одиннадцать вкладок: Debloater/History/About/Activation/Network/Office/Installed/Diagnostics/System/WindowsUpdate/Benchmark; `CatalogTab` была мигрирована ранее, до начала этой серии).

Ветка `mvvm-benchmarktab` от `main`.

## Внешние связи (проверено)

- `MainWindow.xaml.cs:32,221-223` — `_benchmarkTab = new BenchmarkTab();` (кеш), **без подписки на какие-либо события** — самый простой внешний контракт из всех вкладок серии (даже проще WindowsUpdateTab/SystemTab).
- `Ven4Tools.ClientUITests/BenchmarkTabTests.cs` — САМЫЙ содержательный живой UI-тест из всей серии: реально прогоняет тест на быстром профиле (~1 минута, пишет 1 ГиБ на диск), проверяет заполнение `cmbDisks`/`txtConnection`/`cmbVolumes`, честность `txtCeiling` («МБ/с» либо «неизвестно»), отсутствие дублей предупреждений (регресс-тест на гонку `_warningsToken`), переключение текста `btnRunBenchmark` («▶ Запустить тест» ↔ «⏹ Остановить»), заполнение `txtP0Read`, включение `btnCopyReport`, удаление временного файла. Обязательные AutomationId: `cmbDisks`, `txtConnection`, `cmbVolumes`, `txtCeiling`, `cmbProfile`, `btnRunBenchmark`, `txtP0Read` (и по симметрии — `txtP0Name`/`txtP0ReadSub`/`txtP0Write`/`txtP0WriteSub`, `txtP1…txtP3…` — все 4 паттерна), `btnCopyReport`. `KeyButtonsSmokeTests.cs:126` — `btnBenchmarkTab`→`btnRunBenchmark`.
- `Ven4Tools/Services/DiskBenchmark/BenchmarkPresets.Patterns` — 4 паттерна в фиксированном порядке (`SEQ1M Q8T1`, `SEQ1M Q1T1`, `RND4K Q32T16`, `RND4K Q1T1`), ровно совпадает с 4 статичными строками таблицы в оригинальном XAML — не трогаем.

## Архитектурное решение — таблица результатов и AutomationId по индексу

Оригинал строил таблицу результатов через 4 явных `Grid.Row`-блока со статичными `x:Name="txtP{N}Name"`/`txtP{N}Read`/`txtP{N}ReadSub`/`txtP{N}Write`/`txtP{N}WriteSub` (N=0..3), заполняемых через `FindName($"txtP{patternIndex}{suffix}")`. Перенос на биндинг требует `ItemsControl ItemsSource="{Binding ResultRows}"` с `DataTemplate` — но `x:Name` внутри `DataTemplate` не может уникально идентифицировать повторяющиеся инстансы, а живой UI-тест ищет `txtP0Read` по `AutomationId`.

**Решение**: `BenchmarkResultRow` (новый VM-side тип) несёт `Index` (int, позиция паттерна) и вычисляемые строковые свойства `ReadAutomationId => $"txtP{Index}Read"` (и аналогично `NameAutomationId`/`ReadSubAutomationId`/`WriteAutomationId`/`WriteSubAutomationId`), которые биндятся на `AutomationProperties.AutomationId` каждого `TextBlock` внутри `DataTemplate`. Это точно воспроизводит оригинальную схему именования для всех 4 строк, не только для протестированной `txtP0Read`.

## Дизайн VM (`BenchmarkViewModel`, партиалы по образцу code-behind: `.cs`/`.Disks.cs`/`.Run.cs`/`.Report.cs`)

### Вспомогательные типы (в core-файле, как `DiagnosticsTextRow`/`RebootCardInfo` у DiagnosticsViewModel)

```
BenchmarkResultRow { int Index; string Name; string ReadValueText; string ReadSubText; string WriteValueText; string WriteSubText; + 5 вычисляемых *AutomationId }
ConclusionLine { string Text; Brush Foreground; }
DiskOptionItem { string Label; PhysicalDiskInfo Disk; bool CanBenchmark; }
VolumeOptionItem { string Label; BenchmarkVolumeInfo Volume; }
FileSizeOptionItem { string Label; long Bytes; }
```

### Комбобоксы — впервые в серии НИ ОДНОГО TwoWay-риска на TextBox/Slider/CheckBox

Эта вкладка не имеет пользовательского текстового ввода, слайдеров, чекбоксов — только выпадающие списки (реальный `SelectedItem`/`SelectedValue`, TwoWay безопасен на ссылочных/строковых типах с публичным сеттером) и один `ProgressBar`.

- `cmbDisks`/`cmbVolumes`/`cmbFileSize` — `ItemsSource` на `DiskOptions`/`VolumeOptions`/`FileSizeOptions` (списки новых option-типов), `DisplayMemberPath="Label"`, `SelectedItem` TwoWay на `SelectedDiskOption`/`SelectedVolumeOption`/`SelectedFileSizeOption` (публичный set, ссылочный тип — безопасно). `cmbDisks` дополнительно получает `ItemContainerStyle` с `Setter Property="IsEnabled" Value="{Binding CanBenchmark}"` — замена оригинального `IsEnabled = disk.CanBenchmark` на программно созданном `ComboBoxItem`.
- `cmbProfile` — остаётся статичными `ComboBoxItem` в XAML (как в оригинале), `SelectedValuePath="Tag"` `SelectedValue="{Binding ProfileTag, Mode=TwoWay}"` (строка, публичный set, дефолт `"Normal"`) — тот же паттерн, что тема/язык в SystemTab. Статичный `IsSelected="True"` на «Обычный» убирается — дефолт теперь декларативно идёт от VM.
- **`progressBenchmark` (`ProgressBar.Value`, через `RangeBase.Value` — TwoWay по умолчанию) → `ProgressValue` (`private set`) — ОБЯЗАТЕЛЬНО `Mode=OneWay`.** Единственный TwoWay-риск во всей вкладке — тот же класс бага, что уже трижды случался в серии (OfficeTab/DiagnosticsTab/SystemTab).

### Гонка двойного пересчёта предупреждений — перенос 1:1, не упрощать

Оригинал: выбор накопителя программно переставляет и том (`cmbVolumes.SelectedIndex = preferred` внутри `FillVolumes`, вызванного из обработчика выбора диска) — это РЕАЛЬНОЕ вложенное событие `CmbVolumes_SelectionChanged`, которое само вызывает `RefreshWarningsAsync()`, а внешний обработчик диска после `FillVolumes(...)` ТОЖЕ вызывает `RefreshWarningsAsync()` — то есть метод легко вызывается дважды внахлёст, и `_warningsToken` — не защита от гипотетического случая, а обязательный механизм для СУЩЕСТВУЮЩЕГО двойного вызова, покрытый живым UI-тестом `Бенчмарк_ПредупрежденияНеДублируются`.

**Перенос обязан сохранить именно эту двойную природу**, не «оптимизировать» до одного вызова: `SelectedDiskOption`-сеттер должен вызывать `FillVolumeOptions(...)`, которая (когда есть подходящие тома) выставляет `SelectedVolumeOption = <предпочтительный>` ЧЕРЕЗ ПУБЛИЧНЫЙ СЕТТЕР (не напрямую в поле) — это и даёт вложенный вызов `RefreshWarningsAsync()`; затем `SelectedDiskOption`-сеттер сам, после возврата из `FillVolumeOptions`, ещё раз вызывает `RefreshWarningsAsync()` безусловно. Сброс `_selectedVolume`/`SelectedVolumeOption` в начале `FillVolumeOptions` (до перестроения списка) — НАПРЯМУЮ в поле (`SetField` с `nameof`, не публичный сеттер) — соответствует оригинальному `_selectedVolume = null;` (прямое присваивание, не связанное с реальным UI-событием выбора).

### Свойства (core)

`DiskHintText`/`ModelText`/`CapacityText`/`MediaText`/`ConnectionText`/`CeilingText` (string, `private set`, дефолты как в XAML — «Определение накопителей...»/«—»×5), `CeilingBrush` (Brush, `private set`, дефолт `ResolveBrush("TextPrimary")` — статичный дефолт `Foreground` из XAML). `DiskOptions`/`VolumeOptions`/`FileSizeOptions` (`IReadOnlyList<...>`, `private set`, дефолт пустой), `SelectedDiskOption`/`SelectedVolumeOption`/`SelectedFileSizeOption` (публичный set), `ProfileTag` (string, публичный set, дефолт `"Normal"`). `WarningTexts` (`IReadOnlyList<string>`, `private set`, дефолт пустой — каждая строка уже с префиксом `"• "`), `ShowWarnings` (bool, `private set`, дефолт `false`). `RunButtonText` (string, `private set`, дефолт `"▶ Запустить тест"`), `RunStatusText` (string, `private set`, дефолт `"Тест ещё не запускался"`), `IsRunEnabled` (bool, `private set`, дефолт `true` — в оригинальном XAML у `btnRunBenchmark` нет статичного `IsEnabled`, дефолт WPF-кнопки — включена, до первого пересчёта на `Loaded`), `IsControlsEnabled` (bool, `private set`, дефолт `true`), `ShowProgress` (bool, `private set`, дефолт `false`), `ProgressValue` (double, `private set`, дефолт `0`). `ResultRows` (`IReadOnlyList<BenchmarkResultRow>`, `private set`, дефолт — 4 строки-заглушки из `BenchmarkPresets.Patterns` с `"—"`/`""`, как в статичном XAML). `ConclusionLines` (`IReadOnlyList<ConclusionLine>`, `private set`, дефолт — один элемент «Запустите тест, чтобы увидеть разбор результата» с `TextSecondary`). `IsCopyReportEnabled`/`IsSaveReportEnabled` (bool, `private set`, дефолт `false`).

### Команды

`RunBenchmarkCommand` (`RelayCommand.FromAsync`, **без `CanExecute`** — кнопка двухрежимная (Запустить/Остановить), гейт через прямой биндинг `IsRunEnabled` на `Button.IsEnabled`, как и в оригинале единый `.IsEnabled` покрывал оба режима; `CanExecute`-гейт здесь был бы неверен — заблокировал бы кнопку «Остановить» в её собственном рабочем состоянии). `CopyReportCommand`/`SaveReportCommand` (`RelayCommand`, синхронные — оригинальные обработчики тоже синхронные, не `async`).

Ни `OwnerWindowProvider`, ни какие-либо события эта вкладка не использует — оригинал нигде не вызывает `Window.GetWindow(this)`, `SaveFileDialog` не задаёт `Owner`. Самый простой VM-контракт во всей серии.

### `.Disks.cs`

`LoadDisksAsync`/`ShowDiskDetails`/`FillVolumeOptions`/`RefreshWarningsAsync` — переносятся 1:1 (см. разбор гонки выше). `SelectedFileSize`/`SelectedProfile` — приватные вычисляемые свойства (не биндятся), читают `SelectedFileSizeOption?.Bytes`/`ProfileTag` соответственно.

### `.Run.cs`

`RunBenchmarkAsync` (двухрежимная логика: если `_running` — отмена и выход; иначе — валидация, запуск, прогресс, результат, `finally` восстанавливает контролы), `ClearResults`/`ShowResults`/`ShowConclusions` — переносятся 1:1, включая точный порядок построения `ConclusionLines` (те же условия/тексты, что `ShowConclusions`/`AddConclusion` в оригинале).

### `.Report.cs`

`CopyReport`/`SaveReport` — переносятся 1:1, без изменений (нет диалогов с `Owner`).

## XAML

Комбобоксы — биндинг вместо `SelectionChanged=`. Таблица результатов — `ItemsControl` с `DataTemplate` (заголовок «Тест/Чтение/Запись» остаётся статичной строкой над списком, как в оригинале). Предупреждения (`pnlWarningItems`) и выводы (`pnlConclusions`) — `ItemsControl` вместо программного `Children.Add`. Кнопки — `Command=`, `IsEnabled=` на прямых bool-свойствах (не через `CanExecute`, кроме уже оговорённого дизайна `RunBenchmarkCommand`). `progressBenchmark.Value` — обязательный `Mode=OneWay`.

## Тестирование

1. `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental` — 0/0.
2. Грep нового XAML на `progressBenchmark` — обязательно `Mode=OneWay`.
3. Юнит-тесты на `BenchmarkViewModel`: дефолты всех свойств, `RunBenchmarkCommand.CanExecute` (нет предиката — всегда `true`, гейт только через `IsRunEnabled`), гонка `SelectedDiskOption`→`FillVolumeOptions`→`SelectedVolumeOption` (мутационно проверить, что оба уровня действительно вызывают пересчёт — не «оптимизировано» до одного), `BenchmarkResultRow`-AutomationId вычисляются верно по `Index`.
4. Живой UI-прогон на VenchWork — **обязателен и содержателен**: `BenchmarkTabTests` реально прогоняет тест на быстром профиле (~1 минута, создаёт и удаляет временный файл 1 ГиБ) — это единственная вкладка серии, где живой прогон проверяет полный сквозной путь ввода-вывода, не только открытие/навигацию. `KeyButtonsSmokeTests` (`btnBenchmarkTab`) — как обычно.

## Критерий готовности

- Build 0/0.
- Юнит-тесты новые зелёные.
- UI-тесты (`BenchmarkTabTests` — все 4 метода, включая полный прогон теста; `KeyButtonsSmokeTests`) зелёные на VenchWork. Прогон `BenchmarkTabTests` займёт больше времени, чем у любой предыдущей вкладки (~1-2 минуты только на сам тест) — учитывать при оценке норматива 10-15 минут, не поднимать ложную тревогу раньше времени.
- Финальное цельное ревью ветки — обязательный шаг, с явным указанием перепроверить (а) `Mode=OneWay` на `progressBenchmark`, (б) двойной вызов `RefreshWarningsAsync` через каскад `SelectedDiskOption`→`FillVolumeOptions`→`SelectedVolumeOption`, (в) соответствие AutomationId-схемы `txtP{N}...` оригиналу для всех 4 строк.
- Слито в `main`, запушено — без доп. вопроса. UI-прогон на VenchWork дольше 10-15 минут (сверх обычного, с учётом что сам полезный прогон бенчмарка уже около минуты) → эскалация по `feedback_ui_test_hang_escalation`.
- После мерджа — клиент Ven4Tools полностью на MVVM, серия завершена.
