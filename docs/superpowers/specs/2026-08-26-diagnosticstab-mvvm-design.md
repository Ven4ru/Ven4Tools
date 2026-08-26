# DiagnosticsTab — миграция на MVVM (восьмая вкладка после InstalledTab)

## Контекст

`DiagnosticsTab` (741 строка, 6 code-behind файлов: `.xaml.cs` — ядро, `.SystemInfo.cs`, `.TurboBoost.cs`, `.RebootHistory.cs`, `.Report.cs`, `.Checks.cs`) — диагностика ПК: информация о системе, история перезагрузок/сбоев, состояние дисков, ошибки Windows Update, аппаратные/драйверные события, управление Intel Turbo Boost, логи приложения, экспорт полного отчёта.

Работа автономная (ночная сессия) — по аналогии с семью уже смерженными вкладками. Уроки предыдущих вкладок применены: любой TwoWay-биндинг на read-only свойство — крах (Office); перенос `RadioButton.Checked`-без-`Unchecked` в «сеттер реагирует только на true» ломается, если побочный эффект читает соседний флаг немедленно (InstalledTab) — в этой вкладке такого паттерна нет (радиокнопок нет вовсе), но правило учтено на будущее.

**Архитектурное отличие от всех предыдущих вкладок**: `DiagnosticsTab.RebootHistory.cs`/`.Checks.cs` создают DOM-элементы ПРОГРАММНО (`pnlDisks.Children.Add(new TextBlock {...})`, `pnlRebootHistory.Children.Add(BuildRebootCard(d))` — `Expander`+`TextBox`) вместо биндинга на статичную разметку. Это переносится в биндинг на коллекции (`ItemsControl.ItemsSource`) с `DataTemplate`.

**Особый пункт (обнаружено при разборе)**: `tests/Ven4Tools.Tests/ButtonToolTipCoverageTests.cs` содержит `[InlineData("Ven4Tools/Views/Tabs/DiagnosticsTab.RebootHistory.cs", "fixBtn")]` — юнит-тест, который явно сканирует ЭТОТ C#-файл на программно созданную кнопку `fixBtn` (т.к. `AllFunctionalXamlButtonsHaveExplanations` видит только XAML-кнопки). После миграции эта кнопка становится обычной XAML-кнопкой с `ToolTip` — она автоматически попадёт под общее сканирование, а строку `[InlineData(...DiagnosticsTab.RebootHistory.cs..., "fixBtn")]` нужно **удалить** из `ButtonToolTipCoverageTests.cs`, иначе тест упадёт (файл/переменной с таким именем там больше не будет). Это часть Task 2.

**Явное ограничение объёма**: чистый рефакторинг, поведение 1:1, с адаптациями:
1. `this.Dispatcher.Invoke` — в оригинале НЕ используется вовсе (нет фоновых потоков без `await`/`Task.Run` синхронизации через UI-поток напрямую — `Task.Run` в `LoadSystemInfoAsync` оборачивается `await`, результат применяется на UI-потоке естественным образом). В VM тоже не понадобится Dispatcher.Invoke — все точки записи свойств происходят либо в UI-потоке после `await`, либо это чистая синхронная логика.
2. `SystemHealthService`/`AppLogger`/`TrustedExecutablePaths`/`Registry`/`Clipboard`/`MessageBox`/`Process.Start` вызываются из VM напрямую — устоявшийся паттерн.
3. Публичное событие `GoToWindowsUpdate` остаётся на самом `DiagnosticsTab` (внешний контракт, `MainWindow.xaml.cs:213`), VM получает свой `GoToWindowsUpdate`, code-behind ретранслирует — тот же паттерн, что `OfficeTab.GoToActivation`.
4. `_initialized` (защита от повторной инициализации при повторном `Loaded`, когда пользователь уходит с вкладки и возвращается — `MainFrame.Content` пересоздаёт визуальное дерево) остаётся в code-behind — это чисто WPF-lifecycle забота, не VM-концерн (тот же принцип, что уже применялся: `Loaded`-специфика остаётся в code-behind).

**Ветка**: `mvvm-diagnosticstab` (от `main`).

## Внешние связи (проверено)

- `MainWindow.xaml.cs:210,213` — `new DiagnosticsTab()` (кеш `_diagnosticsTab`), подписка на `GoToWindowsUpdate`.
- UI-тесты: `DiagnosticsTabTests.cs` — `btnDiagnosticsTab`→`btnRunDiagnostics` (клик+ожидание), `btnDiagnosticsTab`→`btnCopyFullReport`. `KeyButtonsSmokeTests.cs` — `btnDiagnosticsTab`, `btnCopySystemInfo`. `Top5FeaturesUiTests.cs` — `btnDiagnosticsTab`→`btnOpenWindowsUpdate`. Все эти `x:Name` обязаны сохраниться дословно.
- `tests/Ven4Tools.Tests/ButtonToolTipCoverageTests.cs:36` — см. выше, требует правки в Task 2.

## Архитектура

Новый `Ven4Tools/ViewModels/DiagnosticsViewModel.cs` (ядро, включает вспомогательные типы `DiagnosticsTextRow`/`RebootCardInfo`) + partial-файлы `.SystemInfo.cs`/`.TurboBoost.cs`/`.RebootHistory.cs`/`.Report.cs`/`.Checks.cs` — та же файловая структура, что у code-behind.

### Вспомогательные типы (не INPC — иммутабельные снимки, пересоздаются целиком при каждом прогоне, как `InstalledApp`-строки не пересоздают сам список, а `NetworkCheckResult` пересоздаёт состояние по ссылке)

```
DiagnosticsTextRow { string Text; Brush Foreground; }
RebootCardInfo { string Header; string RawDetails; }
```

### Информация о системе / логи

`OSVersionText`/`ProcessorText`/`RAMText`/`AppVersionText` (string, `private set`, дефолт `"Загрузка..."` для первых трёх — как в XAML `Text="Загрузка..."`, `""` для `AppVersionText` — как в XAML без атрибута `Text`). `LatestLogText` (string, дефолт `"Нажмите «Последний лог» для просмотра..."`).

### Статус-бейдж диагностики

`HealthBadgeText` (string, дефолт `"Диагностика ещё не запускалась"`), `HealthBadgeBrush` (Brush, дефолт — резолв `TextSecondary` тем же паттерном, что в `NetworkViewModel`/`OfficeViewModel`: `(Application.Current?.TryFindResource("TextSecondary") as Brush) ?? Brushes.White`), `LastRunText` (string, дефолт `""`).

### Разделы результатов

`ShowPlaceholders` (bool, дефолт `true`) — единая замена трём статичным XAML-плейсхолдерам («Нажмите «Запустить диагностику»»), т.к. все три секции (диски/история перезагрузок/WU) в оригинале одновременно (в рамках одного прогона `BtnRunDiagnostics_Click`) стирают плейсхолдер `.Children.Clear()` — здесь это один флаг, выставляемый в `false` в начале `RunDiagnosticsAsync()`.

- `DiskRows` (`IReadOnlyList<DiagnosticsTextRow>`, `private set`, дефолт пустой) — заменяет `pnlDisks.Children`.
- `RebootStatusRow` (`DiagnosticsTextRow?`, `private set`) + `ShowRebootStatusRow` (bool) — единственная строка-статус («не найдено» / «недоступно»), когда карточек нет.
- `RebootCards` (`IReadOnlyList<RebootCardInfo>`, `private set`, дефолт пустой) — заменяет `pnlRebootHistory.Children` в ветке «найдены диагнозы».
- `ShowDisableFastStartupButton` (bool) — заменяет условно создаваемую `fixBtn`.
- `WuRows` (`IReadOnlyList<DiagnosticsTextRow>`, `private set`, дефолт пустой) — заменяет `pnlWindowsUpdateFailures.Children` (единый список: и «ошибок не найдено», и до 20 реальных ошибок, и «недоступно» — все три случая в оригинале одинаково являются `TextBlock`-строками).
- `WuButtonsVisible` (bool, дефолт `false`) — единая замена `btnClearWuCache.Visibility`/`btnOpenWindowsUpdate.Visibility` (в оригинале всегда переключаются синхронно вместе).
- `HardwareSummaryText` (string, дефолт `"Нажмите «Запустить диагностику»"`), `HardwareRawText` (string, дефолт `""`), `HardwareRawVisible` (bool, дефолт `false`).

### Turbo Boost

`TurboBoostStatusText` (string, дефолт `"Текущее состояние: определяется..."`).

### Busy-состояния и команды

Только `btnRunDiagnostics` и `btnClearWuCache` в оригинале явно переключают `IsEnabled` — остальные кнопки (Turbo Boost, логи, копирование, экспорт отчёта, фикс быстрого запуска) не имеют защиты от повторного клика в оригинале, и добавлять её самовольно — не 1:1 перенос, а расширение объёма. Следуем оригиналу:

- `IsRunningDiagnostics` (bool) → `RunDiagnosticsCommand.CanExecute: !IsRunningDiagnostics`. Гейт реентерабельности (урок NetworkTab) — `if (IsRunningDiagnostics) return;` первой строкой.
- `IsClearingWuCache` (bool) → `ClearWuCacheCommand.CanExecute: !IsClearingWuCache`, тот же гейт.
- Остальные 9 команд (`CopySystemInfoCommand`, `OpenLogsCommand`, `OpenLatestLogCommand`, `ClearLogsCommand`, `DisableTurboBoostCommand`, `EnableTurboBoostCommand`, `OpenWindowsUpdateCommand`, `CopyFullReportCommand`, `DisableFastStartupCommand`) — обычные `RelayCommand`/`RelayCommand.FromAsync` без `CanExecute` (всегда доступны, как и оригинальные кнопки).

### Порядок прогона диагностики (сохранить дословно)

`RunDiagnosticsAsync()`: гейт → `IsRunningDiagnostics=true` → сброс бейджа/флагов критичности → `_lastRebootDiagnoses = await RunRebootHistoryCheckAsync()` → `await RunDiskCheckAsync()` → `await RunWindowsUpdateCheckAsync()` → `await RunHardwareEventsCheckAsync()` → пересчёт бейджа по `_lastRunHadCritical`/`_lastRunHadWarning` → `finally IsRunningDiagnostics=false`.

## XAML (`DiagnosticsTab.xaml`)

- `pnlRebootHistory`/`pnlDisks`/`txtWuPlaceholder`-обёртка — плейсхолдер-`TextBlock` получает `Visibility` на `ShowPlaceholders` через `BoolToVis`.
- `pnlDisks` → `ItemsControl ItemsSource="{Binding DiskRows}"`, `DataTemplate`: `TextBlock Text="{Binding Text}" Foreground="{Binding Foreground}"`.
- `pnlWindowsUpdateFailures` → аналогично `ItemsControl ItemsSource="{Binding WuRows}"`.
- `pnlRebootHistory`: `TextBlock` (статус-строка) на `RebootStatusRow.Text`/`.Foreground`, `Visibility` на `ShowRebootStatusRow`; `ItemsControl ItemsSource="{Binding RebootCards}"` с `DataTemplate` = `Expander Header="{Binding Header}"` содержащий `TextBox Text="{Binding RawDetails}" IsReadOnly="True" .../` (статичный стиль `CardBackground`/`TextPrimary` через `DynamicResource` прямо в шаблоне — не через VM, т.к. эти цвета константны для всех карточек, не зависят от состояния строки); кнопка «Отключить быстрый запуск» — обычная XAML `Button` с `ToolTip` (тем же текстом, что был в C#) и `Command="{Binding DisableFastStartupCommand}"`, `Visibility` на `ShowDisableFastStartupButton`.
- Остальные биндинги — прямые `Text=`/`Command=`/`Visibility=` по списку выше, без сюрпризов (TwoWay нигде не нужен — эта вкладка не имеет пользовательского ввода, только вывод + кнопки-команды).
- `btnRunDiagnostics`/`btnClearWuCache`: `Command=` без статического `IsEnabled` (CanExecute даёт те же дефолты — RunDiagnostics доступен сразу, ClearWuCache скрыт через `WuButtonsVisible=false`, так что его `CanExecute` неважен пока не показан).

`DiagnosticsTab.xaml.cs`: конструктор создаёт `DiagnosticsViewModel`, `DataContext=_viewModel`, ретранслирует `_viewModel.GoToWindowsUpdate += () => GoToWindowsUpdate?.Invoke();`, `_initialized`-гейт остаётся в code-behind, `Loaded` вызывает `await _viewModel.InitializeAsync()` (обёртка над `LoadSystemInfoAsync()` + `RefreshTurboBoostStatusAsync()`).

## Тестирование

1. `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental` — 0/0.
2. **Обязательно**: `tests/Ven4Tools.Tests/ButtonToolTipCoverageTests.cs` — убрать `InlineData` для `DiagnosticsTab.RebootHistory.cs`/`fixBtn`, убедиться что новая XAML-кнопка `btnDisableFastStartup` покрыта основным тестом `AllFunctionalXamlButtonsHaveExplanations` (у неё есть `ToolTip`).
3. Грep XAML на `Mode=`-риск (урок Office) — в этой вкладке TwoWay не используется вовсе, но проверить всё равно.
4. Юнит-тесты на `DiagnosticsViewModel`: дефолты всех свойств, `CanExecute` `RunDiagnosticsCommand`/`ClearWuCacheCommand` по умолчанию `true`, `ResolveBrush`-хелпер (если вынесен как `internal static`) — фолбэк без `Application.Current`. Реальные WMI/powercfg/EventLog-вызовы не тестируем.
5. Существующие UI-тесты (`DiagnosticsTabTests`, релевантные части `KeyButtonsSmokeTests`/`Top5FeaturesUiTests`) — прогон на VenchWork.
6. Живой клик — не обязателен (автономная сессия).

## Критерий готовности

- Build 0/0.
- `ButtonToolTipCoverageTests` зелёный (включая правку `InlineData`).
- Юнит-тесты новые зелёные.
- UI-тесты зелёные на VenchWork.
- Финальное цельное ревью ветки — обязательный шаг; в предыдущих 7 вкладках подряд находило реальные пробелы.
- Слито в `main`, запушено — без доп. вопроса. Если UI-прогон на VenchWork зависает дольше 10-15 минут — ребут VenchWork / Opus 5 / самостоятельная диагностика, не затягивать (см. `feedback_ui_test_hang_escalation` в памяти).
