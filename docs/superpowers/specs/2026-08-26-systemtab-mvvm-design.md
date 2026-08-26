# SystemTab — миграция на MVVM (девятая вкладка после DiagnosticsTab)

## Контекст

`SystemTab` (1014 строк, 8 code-behind файлов: `.xaml.cs` — ядро, `.Appearance.cs`, `.Settings.cs`, `.AppUpdates.cs`, `.Offline.cs`, `.Cache.cs`, `.Sources.cs`, `.Snapshots.cs`) — вкладка «Настройки»: внешний вид (тема/язык/плотность/анимации), уведомления, установка приложений (тихий режим/папка по умолчанию), область каталога, таймауты, проверка обновлений winget, офлайн-режим и приватность (принудительный офлайн/онлайн/параноидальный режим, офлайн-кэш установщиков с загрузкой и отменой), порядок источников установки, поведение приложения (трей), перенос настроек (экспорт/импорт), скрытые приложения, снапшоты конфигурации (сохранение/восстановление/удаление, с обращением к `DebloaterTab`).

Самая крупная и архитектурно неоднородная вкладка серии — единственная с вложенным `TabControl` из 4 под-вкладок («Общие», «Источники», «Офлайн и приватность», «Профиль и снимки») и с прямой межвкладочной зависимостью (снапшоты читают/применяют твики `DebloaterTab`).

Работа автономная — по аналогии с восемью уже смерженными вкладками. Ветка `mvvm-systemtab` создана от `main`.

## Известная НЕ-цель этой миграции: ThemeService

Аудит `audit_2026_07_18_ui_polish` (см. память) зафиксировал: переключатель темы красит не весь UI — `BrandGreen`/`#4ADE80` захардкожен в ~15+ местах (логотип, `EyebrowStyle`, CTA-кнопки) в обход `AccentColor`. Это осознанно оставлено пользователю как отдельная инициатива («реархитектура темизации»), не начата без подтверждения. **Эта миграция НЕ трогает `ThemeService`/архитектуру тем** — `ThemeService.Apply(...)` вызывается из VM ровно как в оригинале, один-в-один, без попытки исправить известную неполноту.

## Внешние связи (проверено)

- `MainWindow.xaml.cs:199-200` — `new SystemTab()` (кеш `_systemTab`), без подписки на какие-либо события `SystemTab` (в отличие от Office/Diagnostics — здесь внешних `event` нет).
- `MainWindow.xaml.cs:314` — `public DebloaterTab EnsureDebloaterTab()` — уже публичный метод (используется снапшотами).
- `MainWindow.xaml.cs:274` — `public void UpdateTabVisibility()` — вызывается из `ChkOfflineMode_Click`/`ChkForceOnlineMode_Click`.
- `DebloaterTab` (уже мигрирована на MVVM) — публичный контракт: `IReadOnlyList<string> GetSelectedTweakIds()`, `void SetSelectedTweakIds(IReadOnlyCollection<string> ids)`, `Task<(int Succeeded, int Total)> ApplyTweaksByIdsAsync(IReadOnlyCollection<string> ids, IProgress<string>? progress, CancellationToken ct = default)`.
- UI-тесты, обязаны сохраниться дословно (`x:Name`/AutomationId): `btnSystemTab` (в MainWindow), `lstSourceOrder`, `btnSrcUp`, `btnSrcDown`, `btnSaveSourceOrder`, `txtSourceOrderStatus` (`SourceOrderSettingsUiTests.cs`, `AuditFixesUiTests.cs`), `btnCacheSelectAll`, `btnCacheSelectNone`, `btnOpenCacheFolder`, `btnClearCache` (`Phase2SystemTabTests.cs`), `btnSaveSnapshot` (`Phase2SystemTabTests.cs` — реальный сквозной прогон: открывает диалог имени, вводит имя, сохраняет, проверяет что `DisplayLabel` с этим именем появился в списке, удаляет). Под-вкладки ищутся по `TabItem`+`ByName("Источники"|"Офлайн и приватность"|"Профиль и снимки")` — заголовки `Header=` в XAML должны остаться теми же строками.
- `Ven4Tools/Models/ConfigSnapshot.cs:42` — `ConfigSnapshotInfo { FilePath, Name, CreatedAt, TweakCount, PresetCount, string DisplayLabel (computed) }` — существующая модель, НЕ трогаем.
- `Ven4Tools/Models/SourceOrderSettings.cs` — `Winget`/`Choco`/`Direct` константы, `AllSources`, `Labels` (Dictionary), `Mode`/`GlobalOrder`/`CategoryPrimary` — НЕ трогаем.

## Архитектура

Новый `Ven4Tools/ViewModels/SystemViewModel.cs` (ядро) + partial-файлы `.Appearance.cs`/`.Settings.cs`/`.AppUpdates.cs`/`.Offline.cs`/`.Cache.cs`/`.Sources.cs`/`.Snapshots.cs` — та же файловая структура, что у code-behind (8 файлов).

### Кросс-cutting паттерны (важно — здесь их больше, чем в любой предыдущей вкладке)

1. **`OwnerWindowProvider`** (устоявшийся паттерн, `Func<Window?>? OwnerWindowProvider { get; set; }`, см. `ActivationViewModel`/`CatalogViewModel`/`DebloaterViewModel`) — нужен для `SnapshotNameDialog { Owner = ... }` и для `MessageBox.Show` не требуется (тот не принимает Owner в этом проекте нигде явно). Код-бехайнд: `_viewModel.OwnerWindowProvider = () => Window.GetWindow(this);`.
2. **Новый `DebloaterTabProvider`** (`Func<DebloaterTab?>? DebloaterTabProvider { get; set; }`) — тот же паттерн-делегат, специфичный для этой вкладки: код-бехайнд задаёт `_viewModel.DebloaterTabProvider = () => Window.GetWindow(this) is MainWindow mw ? mw.EnsureDebloaterTab() : null;`. VM вызывает `DebloaterTabProvider?.Invoke()` внутри снапшотов ровно там, где оригинал вызывал `GetDebloaterTab()`.
3. **Новый `RefreshTabVisibility`** (`Action? RefreshTabVisibility { get; set; }`) — код-бехайнд: `_viewModel.RefreshTabVisibility = () => { if (Window.GetWindow(this) is MainWindow mw) mw.UpdateTabVisibility(); };`. Вызывается VM из офлайн/принудительно-онлайн чекбоксов.
4. **Анимации/скролл, требующие живой `UIElement`** (`MotionService.CrossFade`/`MotionService.Pulse`, `TextBox.ScrollToEnd()`) остаются заботой code-behind, не VM — тот же принцип, что уже применялся к `Loaded`-специфике. VM поднимает три простых события `event Action? ThemeApplied;`, `event Action? ConnectivityStatusUpdated;` и `event Action? CacheLogAppended;` (без параметров — не несут данных, чисто нотификация "произошло"), code-behind подписывается и на своих именованных элементах (`Window.GetWindow(this) ?? this` для темы, `pnlConnStatus` для статуса связи, `txtCacheLog` для лога кэширования) вызывает `MotionService.CrossFade`/`MotionService.Pulse`/`ScrollToEnd()` с теми же параметрами, что в оригинале (220мс / scale 1.015, 160мс). `CacheLogAppended` — адаптация: оригинал вызывал `txtCacheLog.AppendText(...); txtCacheLog.ScrollToEnd();` при каждой строке прогресса загрузки в кэш; VM лишь копит текст в `CacheLogText` (`Mode=OneWay`-биндинг), автопрокрутку при потоковом обновлении без этого события потеряли бы.
5. **`_initialized`/`_connSubscribed`** (защита повторного `Loaded` + переподписка на `ConnectivityMonitor.StatusChanged` при каждом показе, с отпиской в `Unloaded`) — остаются в code-behind, тот же принцип, что и `_initialized` во всех предыдущих вкладках. `ConnectivityMonitor.StatusChanged` вызывает VM-метод (`UpdateConnectivityStatus()`), но САМА подписка/отписка — лайфсайкл-забота code-behind.

### Явное ограничение объёма

Чистый рефакторинг, поведение 1:1, кроме перечисленных выше делегатов/событий (обязательная адаптация под отсутствие Window/UIElement во VM) и следующих точечных мест:

- **`CacheAppItem`** (сейчас `private sealed class` внутри code-behind, без `INotifyPropertyChanged`, с ручным `listCacheApps.Items.Refresh()` после программного изменения `IsSelected` кнопками «Все»/«Сброс») переносится в `Ven4Tools/ViewModels/CacheAppItem.cs` **с добавлением `INotifyPropertyChanged`** на `IsSelected` — без этого программное изменение (`SelectAll`/`SelectNone` команды) не отразится в уже отрисованных чекбоксах без ручного `Items.Refresh()`, которого в MVVM-биндинге нет и быть не должно (тот же класс адаптации, что уже применялся к `InstalledApp`). Пользовательский клик по чекбоксу работал бы и без INPC (TwoWay-запись не требует INPC на чтение), но программная сторона — требует.
- **Снапшоты**: `ConfigSnapshotInfo` (из `Ven4Tools.Models`, шарится с `ConfigSnapshotService`) не трогаем, но заворачиваем в новый `Ven4Tools/ViewModels/SnapshotRow.cs` (`INotifyPropertyChanged`, `ConfigSnapshotInfo Info { get; }`, `bool IsRestoring { get; private set; }` — заменяет оригинальное `btn.IsEnabled = false` на конкретной нажатой кнопке восстановления; `DisplayLabel => Info.DisplayLabel` для прямого биндинга `Text=`). Восстановление/удаление остаются per-item операциями (не блокируют другие строки списка), как и в оригинале — `RestoreSnapshotCommand`/`DeleteSnapshotCommand` получают `SnapshotRow` через `CommandParameter="{Binding}"` (паттерн `CatalogTab`/`InstalledTab`/`DiagnosticsTab`), `RestoreSnapshotCommand.CanExecute` проверяет `!((SnapshotRow)p).IsRestoring`.
- **`SourceItem`** (тоже `private sealed class` в оригинале) переносится в `Ven4Tools/ViewModels/SourceItem.cs` как простой POCO (`Id`/`Label`, без INPC — свойства не меняются после создания, только порядок в `ObservableCollection` через `.Move()`, что уже уведомляет через `CollectionChanged`).

### Урок InstalledTab — применён с самого начала, не постфактум

Единственная пара радиокнопок в этой вкладке — `rbSourceGlobal`/`rbSourcePerCategory` (режим порядка источников). Ровно тот же паттерн, что уже один раз сломался в InstalledTab (`Checked`-без-`Unchecked`, побочный эффект читает состояние немедленно) — здесь оригинал использует `Click` (не `Checked`) на ОБЕИХ кнопках с одним общим обработчиком `RbSourceMode_Click`, который просто перечитывает `rbSourceGlobal.IsChecked` после клика.

Перенос: **два независимых bool-свойства** `IsGlobalSourceMode`/`IsPerCategorySourceMode`, TwoWay на соответствующие радиокнопки, — точная калька уже проверенного финальным ревью InstalledTab фикса (`IsAllFilterSelected`/`IsUnknownFilterSelected`). Оба сеттера **безусловно** (не только при переходе в `true`) вызывают `UpdateSourcePanels()`, которая просто выставляет `ShowGlobalOrderPanel`/`ShowPerCategoryHint` от текущего `IsGlobalSourceMode`. Безусловный вызов на любое изменение — тот же приём, что закрыл баг InstalledTab; здесь применяется с первого раза, не в фикс-раунде.

### Урок OfficeTab/DiagnosticsTab — TwoWay на read-only свойство

Все TwoWay-по-умолчанию цели этой вкладки перечислены и классифицированы заранее (Task 2 обязан свериться с этим списком, не полагаться только на финальное ревью):

|控트рол | DP | Свойство VM | Сеттер | Комментарий |
|---|---|---|---|---|
| `cmbTheme` | `SelectedItem` (через `SelectionChanged`, не биндинг) | — | — | Остаётся `SelectionChanged`-обработчиком на code-behind-уровне? НЕТ — переносится на `SelectedValue="{Binding ThemeTag, Mode=TwoWay}"` + `SelectedValuePath="Tag"`, `public string ThemeTag { get; set; }` **публичный set** — TwoWay безопасен. |
| `cmbLanguage` | аналогично | `LanguageTag` | публичный set | безопасен |
| `chkCompactMode`/`chkReduceMotion`/`chkMinimizeToTray`/`chkNotifications`/`chkUpdateNotifications`/`chkSilentInstall`/`chkOfflineMode`/`chkForceOnlineMode`/`chkParanoidMode` | `IsChecked` | соответствующие `bool`-свойства | публичный set | безопасны — все под пользовательский ввод, сеттеры пишут в `ProfileService.Current`+`Save()` |
| `sliderCatalogTimeout`/`sliderCheckTimeout` | `Value` (double) | `CatalogTimeoutValue`/`CheckTimeoutValue` (double) | публичный set | безопасны — реальный пользовательский ввод, не read-only вывод |
| `rbSourceGlobal`/`rbSourcePerCategory` | `IsChecked` | `IsGlobalSourceMode`/`IsPerCategorySourceMode` | публичный set | безопасны, см. выше |
| `cmbCatalogMode` | `SelectedValue` | `CatalogModeTag` | публичный set | безопасен |
| `lstSourceOrder` | `SelectedIndex` | `SelectedSourceIndex` (int) | публичный set | безопасен — нужен для `BtnSrcUp`/`BtnSrcDown` |
| `listCacheApps` (`ItemsControl`, `CheckBox.IsChecked` в шаблоне) | `IsChecked` | `CacheAppItem.IsSelected` | публичный set + INPC | безопасен |
| `txtDefaultInstallFolder`/`txtOfflineCachePath`/`txtCacheAppFilter` | `TextBox.Text` | `DefaultInstallFolderText`/`OfflineCachePathText`/`CacheAppFilterText` | публичный set | безопасны — реальный ввод |
| `txtUpdatesLog`/`txtCacheLog`/`txtDefaultInstallFolderStatus`/`txtSourceOrderStatus`/`txtTransferStatus`/`txtHiddenAppsStatus`/`txtSnapshotStatus`/`txtCacheStats`/`txtConnStatus` | `TextBlock.Text` (OneWay по умолчанию — не риск) ИЛИ `TextBox.Text` (`txtUpdatesLog`/`txtCacheLog` — **это `TextBox`, не `TextBlock`!**) | `UpdatesLogText`/`CacheLogText` | **`private set`** | **ОПАСНО — требует явного `Mode=OneWay`**, тот же класс бага, что чинили в DiagnosticsTab (`9b3282f`) и OfficeTab (`29c2609`). Оба — `TextBox` с `IsReadOnly="True"` в оригинале, что НЕ спасает от краха при активации TwoWay-биндинга. **Обязательно `Mode=OneWay` на обоих в Task 2.** |

Этот список — не предположение, а результат построчного разбора `SystemTab.xaml`: `txtUpdatesLog` (`GroupBox "Обновления приложений"`) и `txtCacheLog` (низ под-вкладки «Офлайн и приватность») — единственные `TextBox` с `IsReadOnly="True"`, отображающие вывод программы (не принимающие ввод), то есть кандидаты на тот же баг, что уже дважды находили в этой серии. Помечено явно, чтобы Task 2 не полагался только на финальное ревью третий раз подряд.

## Дизайн по секциям

### Ядро (`SystemViewModel.cs`)

`OwnerWindowProvider`, `DebloaterTabProvider`, `RefreshTabVisibility`, события `ThemeApplied`/`ConnectivityStatusUpdated`, `SetField<T>` (стандартный), конструктор инициализирует все команды и грузит стартовые значения из `ProfileService.Current`/`AppSettings` (эквивалент конструктора `SystemTab()` + `LoadSettings()` + `LoadOfflineSettings()`, без Loaded-специфики — та остаётся в code-behind как `InitializeAsync()`, вызывающий `LoadSourceOrderUI()`/`UpdateCacheStats()`/`LoadCacheAppsList()`/`LoadSnapshotsList()`, ровно как `SystemTab_Loaded` при `_initialized==false`).

`_loadingAppearance`/`_loadingCatalogMode` (защита от срабатывания побочных эффектов при программной инициализации комбобоксов) — остаются как private-поля VM, ровно тот же смысл, что в оригинале.

### `.Appearance.cs`

`ThemeTag`/`LanguageTag` (string, публичный set, сеттеры: `if (_loadingAppearance) return;` → `ProfileService.Current.X = value; ProfileService.Save();` → для темы дополнительно `ThemeService.Apply(value); ThemeApplied?.Invoke();`, для языка — `LocalizationService.Apply(...)` с той же логикой auto→ru/en). `CompactMode`/`ReduceMotion`/`MinimizeToTray` (bool, публичный set, тот же гейт `_loadingAppearance` только у первых двух — `MinimizeToTray` в оригинале гейта не имеет, сохранить это различие).

### `.Settings.cs`

`NotifyInstallComplete`/`NotifyAppUpdates` (bool) — без own-гейта, изменение вызывает `SaveSettings()` (тот же комбинированный метод, что в оригинале — сохраняет и таймауты, и уведомления разом). `CatalogTimeoutValue`/`CheckTimeoutValue` (double) → сеттеры пересчитывают `CatalogTimeoutText`/`CheckTimeoutText` («N сек») и вызывают `SaveSettings()`. `SilentInstall` (bool). `DefaultInstallFolderText` (string) + `DefaultInstallFolderStatusText` (string, `private set`) — логика `ApplyDefaultInstallFolder` переносится как отдельный метод, вызываемый и из `BrowseDefaultInstallFolderCommand` (открывает `FolderBrowserDialog` — WinForms, остаётся вызываться из VM напрямую, устоявшийся паттерн для `SaveFileDialog`/`OpenFileDialog` в других VM), и из сеттера `DefaultInstallFolderText` при потере фокуса — но TwoWay-биндинг `TextBox.Text` пишет на каждое нажатие клавиши, а оригинал применял `LostFocus`, не `TextChanged`. Раз есть публичный сеттер, вызывающий валидацию на КАЖДОЕ нажатие клавиши — это расширение объёма (не 1:1). Решение: `UpdateSourceTrigger=LostFocus` в самом биндинге XAML (не `PropertyChanged`) — сохраняет оригинальное поведение «валидация по потере фокуса» без добавления отдельного code-behind обработчика.

`CatalogModeTag` (string, `_loadingCatalogMode` гейт). `HiddenAppsStatusText` (string, `private set`) + `UnhideAllAppsCommand` (создаёт `new AppManager()` внутри метода — 1:1). `TransferStatusText` (string, `private set`) + `ExportSettingsCommand`/`ImportSettingsCommand` (используют `SaveFileDialog`/`OpenFileDialog` из VM напрямую + `MessageBox.Show` — устоявшийся паттерн; импорт при успехе перезагружает `LoadSettings()`/`LoadOfflineSettings()`/`LoadSourceOrderUI()`/`MinimizeToTray`/`ThemeService.Apply`/`LocalizationService.Init()` — те же вызовы, что в оригинале, плюс `ThemeApplied?.Invoke()` для анимации).

### `.AppUpdates.cs`

`IsCheckingUpdates` (bool) → `CheckUpdatesCommand.CanExecute: !IsCheckingUpdates` (гейт реентерабельности, урок NetworkTab — оригинал явно гасит `btnCheckUpdates.IsEnabled=false`). `UpdatesLogText` (string, **`private set`**, дефолт `"Нажмите «Проверить обновления» для проверки..."` — как в XAML). `ParseUpgradableRows` — переносится как `internal static` (тестируемый хелпер, чистая функция от строки).

### `.Offline.cs`

`OfflineMode`/`ForceOnlineMode`/`ParanoidMode` (bool) — сеттеры `ProfileService.Current.X=value; Save(); RefreshTabVisibility?.Invoke(); UpdateConnectivityStatus();` (первые два) / только `Save()` (параноидальный — не влияет на видимость вкладок). `ConnIconText`/`ConnStatusText`/`ConnStatusBackground` (Brush) — `UpdateConnectivityStatus()` пересчитывает все три + `ConnectivityStatusUpdated?.Invoke()` в конце (для пульса). Цвета — `new SolidColorBrush(Color.FromRgb(...))` те же RGB-константы, что в оригинале (не через `DynamicResource`, оригинал тоже хардкодит).

### `.Cache.cs`

`_httpClient` (`static readonly`, тот же паттерн переиспользуемого клиента) переносится как есть. `CacheStatsText` (string, `private set`). `FilteredCacheApps` (`IReadOnlyList<CacheAppItem>`, `private set`) + `CacheAppFilterText` (string, публичный set, `UpdateSourceTrigger=PropertyChanged` — оригинал фильтрует на каждое нажатие через `TextChanged`, значит здесь TwoWay `PropertyChanged` корректен, в отличие от `DefaultInstallFolderText`) — сеттер пересчитывает `FilteredCacheApps` из приватного мастер-списка `_cacheAppItems`. `SelectAllCacheCommand`/`SelectNoneCacheCommand` — «Все» проходит только по `FilteredCacheApps` (видимым), «Сброс» — по всему `_cacheAppItems` (та же асимметрия, что в оригинале, с тем же обоснованием в комментарии L12). `IsDownloadingToCache` (bool) → `DownloadToCacheCommand.CanExecute`, гейт реентерабельности первой строкой. `CacheLogText` (string, **`private set`**) + `CacheProgressValue` (double, `private set`). **Внимание**: `ProgressBar.Value` (через `RangeBase.Value`) — TwoWay по умолчанию, ровно как `Slider.Value` — это была первопричина краха OfficeTab (`ProgressBar.Value` на `private set`-свойстве, коммит `29c2609`). Биндинг `progressCache.Value` на `CacheProgressValue` **обязан** идти с `Mode=OneWay`. `ShowCacheProgress`/`ShowCacheLog`/`ShowCancelCacheDownload` (bool) — заменяют три независимых `Visibility=Visible/Collapsed`, выставляемых синхронно в оригинале. `CancelCacheDownloadCommand` — без CanExecute-гейта, но сам код внутри отключает себя тем же приёмом, что оригинал (`btnCancelCacheDownload.IsEnabled=false` после клика — заменяется на bool-свойство `CanCancelCacheDownload`, публичный `private set`, привязанное к `IsEnabled` кнопки).

### `.Sources.cs`

`IsGlobalSourceMode`/`IsPerCategorySourceMode` (bool, публичный set — см. раздел про урок InstalledTab выше). `ShowGlobalOrderPanel`/`ShowPerCategoryHint` (bool, `private set`, пересчитываются в `UpdateSourcePanels()`). `SourceItems` (`ObservableCollection<SourceItem>`, публичное свойство-ссылка, не пересоздаётся — `.Move()` уведомляет сам). `SelectedSourceIndex` (int, публичный set). `MoveSourceUpCommand`/`MoveSourceDownCommand` (без параметра, читают `SelectedSourceIndex`, `CanExecute` не обязателен — оригинал просто делает `if (idx <= 0) return;` внутри обработчика, тот же принцип сохраняется как ранний выход внутри команды, не как `CanExecute`, чтобы не расширять объём кнопочным disable, которого не было). `SourceOrderStatusText` (string, `private set`) + `SaveSourceOrderCommand`.

### `.Snapshots.cs`

`Snapshots` (`ObservableCollection<SnapshotRow>`, публичная ссылка). `ShowSnapshotsEmpty` (bool, `private set`, пересчитывается при каждом изменении коллекции). `SnapshotStatusText` (string, `private set`). `IsSavingSnapshot` (bool) → `SaveSnapshotCommand.CanExecute`, гейт реентерабельности. `SaveSnapshotCommand` — открывает `new Views.SnapshotNameDialog(tweakCount, presetCount) { Owner = OwnerWindowProvider?.Invoke() }`, `tweakCount` берётся из `DebloaterTabProvider?.Invoke()?.GetSelectedTweakIds()?.Count ?? 0`, `presetCount` — из `PresetService.LoadAsync()`. `RestoreSnapshotCommand`/`DeleteSnapshotCommand` — `RelayCommand` с параметром `SnapshotRow` (не `RelayCommand.FromAsync` для восстановления — используется async-делегат-параметризованная форма, см. существующую сигнатуру `RelayCommand`/`RelayCommand.FromAsync` в `Ven4Tools/ViewModels/RelayCommand.cs` — если параметризованного `FromAsync` с `CanExecute`-по-параметру ещё нет, задача Task 1 — использовать ровно то, что уже есть в `RelayCommand.cs`, без добавления новых перегрузок, если существующих будет достаточно после чтения файла на этапе реализации).

## XAML

Три из четырёх под-вкладок («Общие», «Офлайн и приватность», «Профиль и снимки») — плоские `StackPanel` с прямыми биндингами `Text=`/`IsChecked=`/`Value=`/`Command=`/`Visibility=` по списку выше, без сюрпризов сверх уже перечисленных TwoWay-рисков. Четвёртая («Источники») — уже описанный радио-паттерн + `ListBox.ItemsSource="{Binding SourceItems}"` с тем же `DataTemplate` (`TextBlock Text="{Binding Label}"`, без изменений). `listCacheApps` (`ItemsControl`) → `ItemsSource="{Binding FilteredCacheApps}"`, `DataTemplate`: `CheckBox Content="{Binding DisplayName}" IsChecked="{Binding IsSelected, Mode=TwoWay}"`. `lstSnapshots` → `ItemsSource="{Binding Snapshots}"`, `DataTemplate`: тот же `Border`+`Grid`, `TextBlock Text="{Binding DisplayLabel}"`, кнопки — `Command="{Binding DataContext.RestoreSnapshotCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}" CommandParameter="{Binding}"` (паттерн `CatalogTab`/`InstalledTab`/`DiagnosticsTab`), аналогично для удаления.

`SystemTab.xaml.cs`: конструктор создаёт `SystemViewModel`, `DataContext=_viewModel`, задаёт три делегата (`OwnerWindowProvider`/`DebloaterTabProvider`/`RefreshTabVisibility`) и подписывается на два события (`ThemeApplied`→`MotionService.CrossFade`, `ConnectivityStatusUpdated`→`MotionService.Pulse(pnlConnStatus,...)`), `_initialized`/`_connSubscribed` гейты остаются как в оригинале (включая подписку/отписку на `ConnectivityMonitor.StatusChanged` в `Loaded`/`Unloaded`, которая теперь дёргает `_viewModel.UpdateConnectivityStatus()` вместо приватного метода code-behind).

## Тестирование

1. `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental` — 0/0.
2. Грep всего нового `SystemTab.xaml` на `Mode=` для `txtUpdatesLog`/`txtCacheLog` — обязательная проверка перед коммитом Task 2, не только на этапе ревью (см. таблицу выше).
3. Юнит-тесты на `SystemViewModel`: дефолты всех свойств по всем 7 партиалам, `CanExecute` для `CheckUpdatesCommand`/`DownloadToCacheCommand`/`SaveSnapshotCommand` по умолчанию `true`, `ParseUpgradableRows` (уже тестируемая чистая функция — перенос существующих сценариев, если были, либо новые), `IsGlobalSourceMode`/`IsPerCategorySourceMode` — мутационно проверить, что оба сеттера безусловно вызывают пересчёт панелей (не только при `true`, по прямой аналогии с фиксом InstalledTab). `CacheAppItem`/`SnapshotRow`/`SourceItem` — INPC там, где заявлено.
4. Живой UI-прогон на VenchWork: `Phase2SystemTabTests` (включает реальное сохранение/удаление снапшота через диалог — самый содержательный тест этой вкладки), `SourceOrderSettingsUiTests`, `AuditFixesUiTests` (те части, что трогают SystemTab — источники), плюс `KeyButtonsSmokeTests`/`ClientUiTests` как обычно.

## Критерий готовности

- Build 0/0.
- Юнит-тесты новые зелёные, весь набор без регрессий.
- UI-тесты (`Phase2SystemTabTests`, `SourceOrderSettingsUiTests`, релевантные части `AuditFixesUiTests`) зелёные на VenchWork.
- Финальное цельное ревью ветки — обязательный шаг, с explicit указанием проверить таблицу TwoWay-рисков (`txtUpdatesLog`/`txtCacheLog`) и урок InstalledTab (`IsGlobalSourceMode`/`IsPerCategorySourceMode`).
- Слито в `main`, запушено — без доп. вопроса. UI-прогон на VenchWork дольше 10-15 минут → эскалация (реб, Opus 5, самостоятельная диагностика) по `feedback_ui_test_hang_escalation`.
