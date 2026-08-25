# InstalledTab — миграция на MVVM (седьмая вкладка после OfficeTab)

## Контекст

`InstalledTab` (771 строка, 5 code-behind файлов: `InstalledTab.xaml.cs` — ядро + класс `InstalledApp`, `InstalledTab.Filters.cs`, `InstalledTab.List.cs`, `InstalledTab.BulkOps.cs`, `InstalledTab.ExportImport.cs`) — список установленных приложений через `winget list`, обновление/удаление по одному и группой, экспорт/импорт списка, фоновая предзагрузка списка ДО открытия вкладки.

Работа автономная (ночная сессия) — решения приняты самостоятельно по аналогии с шестью уже смерженными вкладками. **Урок вкладки Office** (см. `project_ven4tools_mvvm_migration_officetab_2026_08_26` в памяти): любой `RangeBase.Value`/`Selector.SelectedItem`/`TextBox.Text`/`ToggleButton.IsChecked`, биндящийся на VM-свойство без публичного сеттера, ОБЯЗАН иметь явный `Mode=OneWay` — иначе гарантированный крах при активации биндинга. Ниже это учтено явно для каждого такого биндинга.

**Явное ограничение объёма**: чистый рефакторинг, поведение 1:1, с четырьмя адаптациями (все — уже применённый в серии паттерн):
1. `this.Dispatcher.Invoke` → `System.Windows.Application.Current.Dispatcher.Invoke`.
2. `UiGuards.WarnIfInstallBusy()`/`InstallationService.InstallSemaphore`/`AppUninstallService`/`WingetRunner`/`WingetArgs`/`WingetErrorMapper` вызываются из VM напрямую — устоявшийся паттерн.
3. `MessageBox.Show`/`Microsoft.Win32.SaveFileDialog`/`OpenFileDialog` — напрямую из VM (тот же прагматизм, что `Process.Start`/WMI в `ActivationViewModel`/`OfficeViewModel`).
4. **Статический контракт**: `InstalledTab.StartPreload()` (публичный static, вызывается из `MainWindow.xaml.cs:99` ДО того как вкладка вообще создана) остаётся статическим методом на самом `InstalledTab`, но делегирует в новый `internal static` метод `InstalledViewModel.StartPreload()` — статическое состояние предзагрузки (`_preloadTask`/`_cachedRawOutput`/`_preloadLock`) переезжает в `InstalledViewModel` как статические поля (они и раньше были статическими на классе `InstalledTab` — просто теперь класс-владелец другой). `InstalledTab.ShowUpdatesFilter()` (публичный instance-метод, `MainWindow.xaml.cs:169`) остаётся на `InstalledTab`, ретранслирует в `_viewModel.ShowUpdatesFilter()`.

**Ветка**: `mvvm-installedtab` (от `main`), мердж+пуш — сразу после верификации.

## Внешние связи (проверено)

- `MainWindow.xaml.cs:99` — `InstalledTab.StartPreload()` (статический вызов, до создания вкладки).
- `MainWindow.xaml.cs:168-169,190` — `new InstalledTab()` (кешируется в `_installedTab`), `_installedTab.ShowUpdatesFilter()`.
- UI-тесты: `Phase3RemainingTabsTests.InstalledTab_ПроверитьОбновления` — клик `btnInstalledTab` → клик `btnRefresh` (реальный `winget list`, таймаут теста 60с). `KeyButtonsSmokeTests` — `GoTo("btnInstalledTab", "btnRefresh", "Установленные")`, ссылается на `btnRefresh` в двух местах. `AutomationId`, которые обязаны сохраниться дословно: `btnInstalledTab` (кнопка вкладки в MainWindow, не эта вкладка), `btnRefresh`, `txtSearch` (внимание: `txtSearch` есть и на `CatalogTab` — коллизия имён между разными вкладками уже существует до этой миграции, не моя забота).

## Архитектура

Новый `Ven4Tools/ViewModels/InstalledApp.cs` (класс данных строки, переносится как есть из `InstalledTab.xaml.cs`, без изменений тела — уже `INotifyPropertyChanged`) + `Ven4Tools/ViewModels/InstalledViewModel.cs` (ядро) + partial-файлы `InstalledViewModel.Filters.cs`/`InstalledViewModel.List.cs`/`InstalledViewModel.BulkOps.cs`/`InstalledViewModel.ExportImport.cs` — та же файловая структура, что у code-behind (паттерн partial VM уже устоялся: `CatalogViewModel.*`, `OfficeViewModel.*`).

### Список / состояние загрузки

`DisplayedApps` (`IReadOnlyList<InstalledApp>`, INPC, `private set`) — заменяет `lstApps.ItemsSource`. `IsLoading`/`IsEmpty`/`IsListVisible` (bool, INPC, `private set`) — заменяют `ShowState("loading"/"empty"/"list")`, ровно одно `true` одновременно (как и оригинал — три взаимоисключающих `Visibility`). `LoadingMessage` (string, INPC, `private set`, default `"⏳ Получение списка установленных приложений..."`).

### Фильтры / сортировка

`IsAllFilterSelected`/`IsUnknownFilterSelected` (bool, `Mode=TwoWay`) — те же 2 радиокнопки, что `rdbAll`/`rdbUnknown`. Как и в спеке OfficeTab, инвалидация (здесь — `ApplyFilter()`) вызывается ТОЛЬКО при переходе в `true` — эквивалент `Checked="FilterChanged"` без `Unchecked` в оригинале (оригинал не слушает `Unchecked` для радиокнопок). `OnlyUpdates` (bool, `Mode=TwoWay`) — `ApplyFilter()` при ЛЮБОМ изменении (оригинал слушает и `Checked`, и `Unchecked`). `SearchText` (string, `Mode=TwoWay`) — `ApplyFilter()` при любом изменении. `SortIndex` (int, `Mode=TwoWay`) — `ApplyFilter()` при любом изменении; значения 0/1/2 соответствуют трём `ComboBoxItem` (по имени/по версии/сначала с обновлениями), логика сортировки переносится как есть.

`StatsText` (string, INPC, `private set`) — заменяет `txtStats.Text`.

### Выбор строк / групповые кнопки

`SelectAllState` (`bool?`, `Mode=TwoWay`) — заменяет `chkSelectAll`. Сеттер, получив `true`/`false` от биндинга (WPF-клик по `CheckBox` с `IsThreeState` по умолчанию `False` даёт только `true`/`false`, никогда `null` от пользователя — как и в оригинале), выполняет ровно ту же логику, что `ChkSelectAll_Click`: проставляет `IsSelected` всем видимым строкам, где `CanAct && HasUpdate`, в значение из клика. **Важно**: в оригинале `ChkSelectAll_Click` НЕ пересчитывает сам `chkSelectAll.IsChecked` после — он остаётся тем, что выставил пользователь (это корректно, т.к. после единообразного проставления всех подходящих строк индикатор и так соответствует истине). Переносим 1:1: сеттер `SelectAllState` не перезаписывает сам себя после операции.

Каждая строчная чекбокс-строка (`IsSelected` на `InstalledApp`) при клике в оригинале ДОПОЛНИТЕЛЬНО вызывает `ItemCheckBox_Click` → `UpdateUpdateSelectedButton()` + `UpdateSelectAllState()`. В XAML это сохраняется как `Command="{Binding DataContext.RowSelectionChangedCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"` РЯДОМ с `IsChecked="{Binding IsSelected, Mode=TwoWay}"` — WPF позволяет `Command` и `IsChecked`-биндинг сосуществовать на одном `CheckBox` (оба реагируют на клик независимо, `Command` не подменяет toggle-логику). `RecomputeSelectAllState()` — приватный метод VM, портирует `UpdateSelectAllState()`. `RecomputeCanActOnSelection()` — портирует `UpdateUpdateSelectedButton()`, выставляет `CanUpdateSelected`/`CanUninstallSelected` (bool, INPC) — заменяют `btnUpdateSelected.IsEnabled`/`btnUninstallSelected.IsEnabled`.

**Важная деталь порядка вызовов** (сохранить дословно): `ApplyFilter()` в оригинале вызывает `UpdateStats()` + `UpdateSelectAllState()`, но **не** `UpdateUpdateSelectedButton()` — то есть после смены фильтра/поиска enable-состояние кнопок «Обновить»/«Удалить» (групповых) не пересчитывается, только tri-state чекбокса. Тот же (возможно нежелательный, но не наш баг) пробел переносится 1:1: `ApplyFilter()` вызывает `RecomputeStats()` + `RecomputeSelectAllState()`, не `RecomputeCanActOnSelection()`.

### Кнопки-команды и их busy-состояния

| Оригинал (что реально disable/enable) | VM |
|---|---|
| `btnRefresh.IsEnabled` — `false` во время своего клика; ТАКЖЕ `false` во время `BtnUpgradeAll_Click` (общий с `btnUpgradeAll`) | `RefreshCommand.CanExecute: !IsRefreshing && !IsUpgradingAll` |
| `btnUpgradeAll.IsEnabled` — `false` только во время своего клика | `UpgradeAllCommand.CanExecute: !IsUpgradingAll` |
| `btnExport.IsEnabled` — `false` только во время своего клика | `ExportCommand.CanExecute: !IsExporting` |
| `btnImport.IsEnabled` — `false` только во время своего клика (устанавливается ПОСЛЕ диалога подтверждения и `UiGuards`-проверки, не в начале обработчика) | `ImportCommand.CanExecute: !IsImporting` |
| `btnUpdateSelected.IsEnabled` — вычисляемое (есть ли среди выбранных хоть один `HasUpdate`), плюс явный `false` во время `BtnUpdateSelected_Click` | `UpdateSelectedCommand.CanExecute: CanUpdateSelected && !IsUpdatingSelected` |
| `btnUninstallSelected.IsEnabled` — вычисляемое (выбрано ли что-то), плюс явный `false`/восстановление во время `BtnUninstallSelected_Click` | `UninstallSelectedCommand.CanExecute: CanUninstallSelected && !IsUninstallingSelected` |
| Кнопки «Обновить»/«Удалить» в строке — `IsEnabled="{Binding CanAct}"` (`!IsProcessing` на `InstalledApp`) | без изменений — `CanAct` уже на `InstalledApp`, не трогаем |

Гейт реентерабельности (урок NetworkTab): каждая из шести command-методов начинается с `if (СвойБизиФлаг) return;` первой строкой, до любой другой логики — тот же паттерн, что `RunDownloadAsync`/`RunInstallAsync` в `OfficeViewModel`.

### Загрузка (`LoadAppsAsync`, `StartPreload`)

Переносятся как есть, включая статическую блокировку `_preloadLock` и точную семантику «предзагрузка была — ждём её и потребляем кэш; предзагрузки не было — идём в `winget list` напрямую». `ParseWingetList`/`Extract` — чистые статические методы, переносятся без изменений (уже тестируемы как есть, если потребуется — но winget-парсинг не входит в юнит-тесты этой задачи, см. ниже).

### Групповые операции (`BulkOps`)

`UpgradeAllAsync`/`UpdateAppAsync`/`UninstallAppAsync`/`UpdateSelectedAsync`/`UninstallSelectedAsync` переносятся как есть — `MessageBox.Show`-подтверждения, `UiGuards.ConfirmAndCreateRestorePointAsync`, `InstallationService.InstallSemaphore`, `DescribeWingetExitCode`. `UpdateAppCommand`/`UninstallAppCommand` — параметризованные команды (`CommandParameter="{Binding}"` — сам `InstalledApp` строки), паттерн `Command="{Binding DataContext.XCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"` уже устоялся в `CatalogTab.xaml` (см. `OpenCardCommand`/`ToggleFavoriteCommand` и др.) — используем тот же, а не изобретаем новый.

### Экспорт / импорт

`ExportAsync`/`ImportAsync` переносятся как есть, включая точный текст диалогов подтверждения и порядок проверок (`UiGuards.WarnIfInstallBusy()` — ранний выход ДО любых UI-мутаций, как явно откомментировано в оригинале).

## XAML (`InstalledTab.xaml`)

- 2 `RadioButton` (`rdbAll`/`rdbUnknown`): `IsChecked="{Binding IsAllFilterSelected, Mode=TwoWay}"`/`IsUnknownFilterSelected`.
- `chkOnlyUpdates`: `IsChecked="{Binding OnlyUpdates, Mode=TwoWay}"`.
- `cmbSort`: `SelectedIndex="{Binding SortIndex, Mode=TwoWay}"`.
- `txtSearch`: `Text="{Binding SearchText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"` (оригинал реагирует на `TextChanged`, то есть на каждое нажатие клавиши — `UpdateSourceTrigger=PropertyChanged` обязателен, иначе биндинг по умолчанию для `TextBox.Text` синхронизируется по `LostFocus`, и живой поиск при вводе сломается).
- `btnRefresh`/`btnUpgradeAll`/`btnExport`/`btnImport`/`btnUpdateSelected`/`btnUninstallSelected`: `Command="{Binding ...Command}"`, статический `IsEnabled="False"` убирается у `btnUpdateSelected`/`btnUninstallSelected` (эти два в оригинале стартуют `False` статически в XAML — `CanExecute` даёт тот же дефолт, т.к. `CanUpdateSelected`/`CanUninstallSelected` по умолчанию `false`).
- `chkSelectAll`: `IsChecked="{Binding SelectAllState, Mode=TwoWay}"` (тип `bool?` совместим с `CheckBox.IsChecked`, никакого конвертера не нужно).
- `txtStats`: `Text="{Binding StatsText}"`.
- `pnlLoading`/`pnlEmpty`/`listScroll` — `Visibility` через `BoolToVis`-конвертер на `IsLoading`/`IsEmpty`/`IsListVisible` соответственно (конвертер уже объявлен в `UserControl.Resources`).
- `txtLoadingMsg`: `Text="{Binding LoadingMessage}"`.
- `ItemsControl x:Name="lstApps"`: `ItemsSource="{Binding DisplayedApps}"`.
- Внутри `DataTemplate` (тип элемента — `InstalledApp`, без изменений своих же биндингов `IsSelected`/`Name`/`Version`/`HasUpdate`/`Available`/`SourceDisplay`/`IsVerified`/`IsUnknownSource`/`CanAct` — они уже были прямыми биндингами на `InstalledApp`, не на code-behind, и не меняются):
  - Чекбокс строки: добавить `Command="{Binding DataContext.RowSelectionChangedCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"` рядом с уже существующим `IsChecked="{Binding IsSelected, Mode=TwoWay}"`.
  - Кнопка «Обновить» строки: `Command="{Binding DataContext.UpdateAppCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"` + `CommandParameter="{Binding}"` вместо `Tag="{Binding}"` + `Click="BtnUpdate_Click"`.
  - Кнопка «Удалить» строки: аналогично `UninstallAppCommand`.

`InstalledTab.xaml.cs`: конструктор создаёт `InstalledViewModel`, `DataContext = _viewModel`, `Loaded += (_, _) => _ = _viewModel.LoadAppsAsync();` (прямой вызов метода, не через команду — оригинал тоже напрямую вызывает метод из `Loaded`, не через `Click`). Публичные члены: `public static void StartPreload() => InstalledViewModel.StartPreload();`, `public void ShowUpdatesFilter() => _viewModel.ShowUpdatesFilter();`.

## Тестирование (порядок обязателен, как у предыдущих шести вкладок)

1. `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental` — 0/0 после каждого шага.
2. **Перед финальным ревью — обязательный грep XAML на `Mode=OneWay`-риск** (урок Office): любой `Value="{Binding X}"` / `SelectedItem="{Binding X}"` / `Text="{Binding X}"` (на `TextBox`, не `TextBlock`) / `IsChecked="{Binding X}"` без явного `Mode=` — проверить, что либо `X` имеет публичный сеттер (тогда TwoWay безопасен и обычно уместен), либо стоит явный `Mode=OneWay`.
3. Юнит-тесты на `InstalledViewModel`: дефолты всех свойств при конструировании, `CanExecute` каждой команды в состоянии по умолчанию (`UpdateSelectedCommand`/`UninstallSelectedCommand` — `false`, остальные четыре — `true`), сортировка/фильтрация как чистая функция (если будет вынесена — см. ниже), `SelectAllState` сеттер (проставляет `IsSelected` подходящим строкам). Реальные `winget`-вызовы, диалоги, `MessageBox` не тестируем (как и раньше). `dotnet test` — только на VenchWork.
4. Существующий `InstalledTab_ПроверитьОбновления` (`Phase3RemainingTabsTests.cs`, таймаут 60с — реальный `winget list`) + `KeyButtonsSmokeTests` — прогон на VenchWork.
5. Живой ручной клик — не обязателен (автономная сессия).

## Критерий готовности

- Build 0/0.
- Юнит-тесты новые зелёные.
- Грep на `Mode=OneWay`-риск выполнен и задокументирован в отчёте задачи.
- `InstalledTab_ПроверитьОбновления` и `KeyButtonsSmokeTests` зелёные на VenchWork.
- Финальное цельное ревью ветки — обязательный шаг; в предыдущих 5 вкладках подряд находило реальные пробелы (в шестой — реальный краш-баг).
- Слито в `main`, запушено — без доп. вопроса.
