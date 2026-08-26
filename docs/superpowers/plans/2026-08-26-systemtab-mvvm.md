# SystemTab MVVM Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Перенести логику вкладки «Настройки» (`SystemTab`, 1014 строк в 8 partial-файлах code-behind, вложенный `TabControl` из 4 под-вкладок) из code-behind в `SystemViewModel`, оставив `SystemTab.xaml`/`.xaml.cs` тонкой обёрткой. Девятая вкладка серии MVVM-миграции — крупнейшая и самая архитектурно неоднородная.

**Architecture:** `SystemViewModel : INotifyPropertyChanged`, partial-класс по образцу `DiagnosticsViewModel.*` — `SystemViewModel.cs` (ядро) + `.Appearance.cs`/`.Settings.cs`/`.AppUpdates.cs`/`.Offline.cs`/`.Cache.cs`/`.Sources.cs`/`.Snapshots.cs` (та же файловая структура, что у code-behind). Три вспомогательных типа (`CacheAppItem`, `SourceItem`, `SnapshotRow`) переносятся из приватных nested-классов code-behind в собственные файлы `Ven4Tools/ViewModels/`. Кросс-cutting зависимости от `Window`/другой вкладки (`DebloaterTab`) решены делегатами-свойствами (`OwnerWindowProvider`/`DebloaterTabProvider`/`RefreshTabVisibility`), устоявшийся паттерн этого проекта (см. `ActivationViewModel`/`CatalogViewModel`/`DebloaterViewModel`). Анимации/автопрокрутка, которым нужен живой `UIElement`, остаются в code-behind, триггерятся тремя событиями VM (`ThemeApplied`/`ConnectivityStatusUpdated`/`CacheLogAppended`).

**Tech Stack:** .NET 8, WPF, xUnit.

## Global Constraints

- Поведение 1:1 с оригиналом, кроме адаптаций:
  1. `ThemeService`/`AppLogger`/`ProfileService`/`AppSettings`/`ConnectivityMonitor`/`OfflineService`/`SourceOrderService`/`ConfigSnapshotService`/`PresetService`/`ProfileExportService`/`HashHelper`/`CommandLineGuard`/`CatalogLoaderService`/`WingetRunner`/`WingetArgs`/`UiGuards`/`MessageBox`/`Process.Start`/`SaveFileDialog`/`OpenFileDialog`/`FolderBrowserDialog` — из VM напрямую (устоявшийся паттерн).
  2. **Этой миграцией НЕ трогается `ThemeService`/архитектура тем.** Известная неполнота (переключатель темы красит не весь UI — `audit_2026_07_18_ui_polish`) — осознанно оставлена пользователю как отдельная инициатива, вне объёма рефакторинга.
  3. `OwnerWindowProvider` (`Func<Window?>?`) — для `SnapshotNameDialog`. `DebloaterTabProvider` (`Func<Ven4Tools.Views.Tabs.DebloaterTab?>?`) — для снапшотов (чтение/применение твиков). `RefreshTabVisibility` (`Action?`) — для `MainWindow.UpdateTabVisibility()` из офлайн/принудительно-онлайн чекбоксов. Все три — settable-свойства VM, code-behind задаёт их в конструкторе `SystemTab()`.
  4. Три события `event Action? ThemeApplied;`, `event Action? ConnectivityStatusUpdated;`, `event Action? CacheLogAppended;` — чистая нотификация без данных, code-behind подписывается и вызывает `MotionService.CrossFade`/`MotionService.Pulse(pnlConnStatus,...)`/`txtCacheLog.ScrollToEnd()` на своих именованных элементах.
  5. `_initialized`/`_connSubscribed` (защита повторного `Loaded` + переподписка на `ConnectivityMonitor.StatusChanged` с отпиской в `Unloaded`) — остаются в code-behind, WPF-lifecycle забота, не VM-концерн.
  6. `CacheAppItem` (в оригинале — `private sealed class` без `INotifyPropertyChanged`, синхронизировался вручную через `Items.Refresh()`) переносится в `Ven4Tools/ViewModels/CacheAppItem.cs` **с добавлением `INotifyPropertyChanged`** на `IsSelected` — без него программные «Выбрать все»/«Сброс» не отразятся в уже отрисованных чекбоксах.
  7. `SnapshotRow` (новый тип-обёртка над существующим, НЕ трогаемым `Ven4Tools.Models.ConfigSnapshotInfo`) добавляет `IsRestoring` (`internal set`, INPC) — заменяет `btn.IsEnabled = false` на конкретной нажатой кнопке восстановления в оригинале; per-item, не блокирует другие строки списка.
  8. `SourceItem` — простой POCO без INPC (переставляется только порядком в `ObservableCollection`, что уже уведомляет через `CollectionChanged`).
- **Урок InstalledTab, применён с первого раза**: единственная пара радиокнопок (`rbSourceGlobal`/`rbSourcePerCategory`) переносится как ДВА независимых bool-свойства (`IsGlobalSourceMode`/`IsPerCategorySourceMode`), TwoWay на соответствующие радиокнопки, оба сеттера **безусловно** (не только при `value==true`) вызывают пересчёт видимости панелей — точная калька уже проверенного финальным ревью фикса InstalledTab (`SetFilterFlag`).
- **Урок OfficeTab/DiagnosticsTab — TwoWay на read-only свойство.** Полный список TwoWay-по-умолчанию целей и их сеттеров:
  - `cmbTheme`/`cmbLanguage`/`cmbCatalogMode` (`SelectedValue`) → `ThemeTag`/`LanguageTag`/`CatalogModeTag` — **публичный set**, безопасны.
  - `chkCompactMode`/`chkReduceMotion`/`chkMinimizeToTray`/`chkNotifications`/`chkUpdateNotifications`/`chkSilentInstall`/`chkOfflineMode`/`chkForceOnlineMode`/`chkParanoidMode` (`IsChecked`) — все **публичный set**, безопасны.
  - `sliderCatalogTimeout`/`sliderCheckTimeout` (`Value`) → `CatalogTimeoutValue`/`CheckTimeoutValue` — **публичный set**, безопасны (реальный пользовательский ввод).
  - `rbSourceGlobal`/`rbSourcePerCategory` (`IsChecked`) → `IsGlobalSourceMode`/`IsPerCategorySourceMode` — **публичный set**, безопасны.
  - `lstSourceOrder` (`SelectedIndex`) → `SelectedSourceIndex` — **публичный set**, безопасен.
  - `listCacheApps` (`CheckBox.IsChecked` в шаблоне) → `CacheAppItem.IsSelected` — **публичный set + INPC**, безопасен.
  - `txtDefaultInstallFolder`/`txtOfflineCachePath`/`txtCacheAppFilter` (`TextBox.Text`) → `DefaultInstallFolderText`/`OfflineCachePathText`/`CacheAppFilterText` — **публичный set**, безопасны (реальный ввод; первые два — `UpdateSourceTrigger=LostFocus`, третий — `PropertyChanged`, сохраняя оригинальные триггеры сохранения/фильтрации).
  - **`txtUpdatesLog`/`txtCacheLog` (`TextBox.Text`, `IsReadOnly="True"`) → `UpdatesLogText`/`CacheLogText` — `private set`, ОБЯЗАТЕЛЬНО `Mode=OneWay`.** Тот же класс бага, что уже дважды находили в этой серии (OfficeTab `29c2609`, DiagnosticsTab `9b3282f`) — `IsReadOnly="True"` не спасает, WPF бросает `InvalidOperationException` при АКТИВАЦИИ TwoWay-биндинга, не при попытке записи.
  - **`progressCache` (`ProgressBar.Value`, через `RangeBase.Value`) → `CacheProgressValue` — `private set`, ОБЯЗАТЕЛЬНО `Mode=OneWay`.** `RangeBase.Value` TwoWay по умолчанию — это была первопричина краха OfficeTab (`ProgressBar.Value` на `private set`, коммит `29c2609`); в SystemTab тоже есть `ProgressBar`, тот же риск.
  - Все `TextBlock.Text`/`Border.Background`/`Ellipse.Fill`/`ItemsControl.ItemsSource`/`UIElement.Visibility`/`ButtonBase.Command` — OneWay по умолчанию, риска нет.
- **Гейт реентерабельности** (урок NetworkTab): `RunCheckUpdatesAsync`/`RunDownloadToCacheAsync`/`RunSaveSnapshotAsync` начинаются с `if (СвойБизиФлаг) return;` первой строкой. `RunRestoreSnapshotAsync` — per-item гейт через `row.IsRestoring`. Остальные команды в оригинале не имеют защиты от повторного клика — не добавлять её самовольно.
- Никакой статический `IsEnabled` на кнопках не нужен, кроме `btnCancelCacheDownload` (`IsEnabled="{Binding CanCancelCacheDownload}"` — прямая калька оригинального `btnCancelCacheDownload.IsEnabled=false` после клика, не через `CanExecute`).
- Все `x:Name`, участвующие в UI-тестах, сохраняются дословно: `btnSystemTab` (MainWindow), `lstSourceOrder`, `btnSrcUp`, `btnSrcDown`, `btnSaveSourceOrder`, `txtSourceOrderStatus`, `btnCacheSelectAll`, `btnCacheSelectNone`, `btnOpenCacheFolder`, `btnClearCache`, `btnSaveSnapshot`. Заголовки под-вкладок (`TabItem Header=`) остаются той же строкой: «Общие», «Источники», «Офлайн и приватность», «Профиль и снимки».
- Коммиты — на русском, без Claude/AI-атрибуции.
- Ветка `mvvm-systemtab` уже создана от `main`, спека закоммичена и один раз исправлена (`4672464`, `78bb385`).

---

### Task 1: `SystemViewModel` (3 вспомогательных типа + 8 файлов VM) + юнит-тесты

**Files:**
- Create: `Ven4Tools/ViewModels/CacheAppItem.cs`
- Create: `Ven4Tools/ViewModels/SourceItem.cs`
- Create: `Ven4Tools/ViewModels/SnapshotRow.cs`
- Create: `Ven4Tools/ViewModels/SystemViewModel.cs`
- Create: `Ven4Tools/ViewModels/SystemViewModel.Appearance.cs`
- Create: `Ven4Tools/ViewModels/SystemViewModel.Settings.cs`
- Create: `Ven4Tools/ViewModels/SystemViewModel.AppUpdates.cs`
- Create: `Ven4Tools/ViewModels/SystemViewModel.Offline.cs`
- Create: `Ven4Tools/ViewModels/SystemViewModel.Cache.cs`
- Create: `Ven4Tools/ViewModels/SystemViewModel.Sources.cs`
- Create: `Ven4Tools/ViewModels/SystemViewModel.Snapshots.cs`
- Test: `tests/Ven4Tools.Tests/SystemViewModelTests.cs`

**Interfaces:**
- Consumes: `Ven4Tools.Services.*` (перечислены в Global Constraints п.1), `Ven4Tools.Models.App`/`ConfigSnapshotInfo`/`ConfigSnapshot`/`SourceOrderSettings`, `Ven4Tools.Views.SnapshotNameDialog`, `Ven4Tools.Views.UiGuards`/`RestorePointOutcome`, `Ven4Tools.Views.Tabs.DebloaterTab` (публичный контракт: `IReadOnlyList<string> GetSelectedTweakIds()`, `void SetSelectedTweakIds(IReadOnlyCollection<string>)`, `Task<(int Succeeded, int Total)> ApplyTweaksByIdsAsync(IReadOnlyCollection<string>, IProgress<string>?, CancellationToken)`), `Ven4Tools.Shared.MotionService`, `Ven4Tools.ViewModels.RelayCommand`/`RelayCommand.FromAsync`.
- Produces: `Ven4Tools.ViewModels.CacheAppItem`/`SourceItem`/`SnapshotRow`, `Ven4Tools.ViewModels.SystemViewModel` — публичные свойства/команды по всем 7 партиалам (полный список — см. код ниже); делегаты `OwnerWindowProvider`/`DebloaterTabProvider`/`RefreshTabVisibility`; события `ThemeApplied`/`ConnectivityStatusUpdated`/`CacheLogAppended`; публичные методы `Initialize()`/`UpdateConnectivityStatus()`; `internal static List<string> ParseUpgradableRows(string)`.

- [ ] **Step 1: Создать `Ven4Tools/ViewModels/CacheAppItem.cs`**

```csharp
using System.ComponentModel;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Строка списка приложений для офлайн-кэширования. В оригинальном code-behind —
    /// private nested class без INotifyPropertyChanged; синхронизация с UI шла через
    /// ручной listCacheApps.Items.Refresh() после программного изменения IsSelected
    /// («Выбрать все» / «Сброс»). В MVVM такого механизма нет — IsSelected обязан
    /// поднимать PropertyChanged сам, иначе программные изменения не отразятся
    /// в уже отрисованных чекбоксах.
    /// </summary>
    public sealed class CacheAppItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public required string Id          { get; init; }
        public required string DisplayName { get; init; }
        public required string DownloadUrl { get; init; }
        public required string Sha256      { get; init; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}
```

- [ ] **Step 2: Создать `Ven4Tools/ViewModels/SourceItem.cs`**

```csharp
namespace Ven4Tools.ViewModels
{
    /// <summary>Строка списка порядка источников установки. Переставляется только
    /// порядком в ObservableCollection (.Move()) — свойства после создания не меняются,
    /// INotifyPropertyChanged не нужен.</summary>
    public sealed class SourceItem
    {
        public required string Id    { get; init; }
        public required string Label { get; init; }
    }
}
```

- [ ] **Step 3: Создать `Ven4Tools/ViewModels/SnapshotRow.cs`**

```csharp
using System.ComponentModel;
using Ven4Tools.Models;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Обёртка над Ven4Tools.Models.ConfigSnapshotInfo (не трогаем — шарится с
    /// ConfigSnapshotService) для per-item состояния «идёт восстановление». Заменяет
    /// оригинальное btn.IsEnabled = false на конкретной нажатой кнопке восстановления:
    /// только эта строка блокируется, остальные снапшоты остаются доступны для
    /// восстановления/удаления — точно как в оригинале.
    /// </summary>
    public sealed class SnapshotRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public ConfigSnapshotInfo Info { get; }

        public SnapshotRow(ConfigSnapshotInfo info) => Info = info;

        public string DisplayLabel => Info.DisplayLabel;

        private bool _isRestoring;
        public bool IsRestoring
        {
            get => _isRestoring;
            internal set
            {
                if (_isRestoring == value) return;
                _isRestoring = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRestoring)));
            }
        }
    }
}
```

- [ ] **Step 4: Создать `Ven4Tools/ViewModels/SystemViewModel.cs`**

```csharp
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Ven4Tools.Services;
using Ven4Tools.Views.Tabs;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// ViewModel вкладки «Настройки». Логика перенесена из code-behind при
    /// MVVM-миграции (2026-08-26, девятая вкладка после Debloater/History/About/
    /// Activation/Network/Office/Installed/Diagnostics) без изменения поведения —
    /// см. docs/superpowers/specs/2026-08-26-systemtab-mvvm-design.md.
    /// Разбит на partial-файлы по образцу DiagnosticsViewModel.*.
    /// </summary>
    public sealed partial class SystemViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Окно-владелец для модальных диалогов (SnapshotNameDialog).</summary>
        public Func<Window?>? OwnerWindowProvider { get; set; }

        /// <summary>
        /// Доступ к DebloaterTab для снапшотов (чтение/применение отмеченных твиков) —
        /// вкладка живёт в MainWindow, VM её не создаёт и не хранит.
        /// </summary>
        public Func<DebloaterTab?>? DebloaterTabProvider { get; set; }

        /// <summary>Просит MainWindow пересчитать видимость офлайн-зависимых вкладок.</summary>
        public Action? RefreshTabVisibility { get; set; }

        /// <summary>Тема применена — code-behind проигрывает CrossFade на своём окне.</summary>
        public event Action? ThemeApplied;

        /// <summary>Статус связи пересчитан — code-behind проигрывает Pulse на pnlConnStatus.</summary>
        public event Action? ConnectivityStatusUpdated;

        /// <summary>Строка добавлена в лог кэширования — code-behind прокручивает txtCacheLog вниз.</summary>
        public event Action? CacheLogAppended;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool _loadingAppearance = true;
        private bool _loadingCatalogMode;

        public RelayCommand CheckUpdatesCommand { get; }
        public RelayCommand BrowseDefaultInstallFolderCommand { get; }
        public RelayCommand UnhideAllAppsCommand { get; }
        public RelayCommand ExportSettingsCommand { get; }
        public RelayCommand ImportSettingsCommand { get; }
        public RelayCommand SelectAllCacheCommand { get; }
        public RelayCommand SelectNoneCacheCommand { get; }
        public RelayCommand BrowseCachePathCommand { get; }
        public RelayCommand OpenCacheFolderCommand { get; }
        public RelayCommand ClearCacheCommand { get; }
        public RelayCommand DownloadToCacheCommand { get; }
        public RelayCommand CancelCacheDownloadCommand { get; }
        public RelayCommand MoveSourceUpCommand { get; }
        public RelayCommand MoveSourceDownCommand { get; }
        public RelayCommand SaveSourceOrderCommand { get; }
        public RelayCommand SaveSnapshotCommand { get; }
        public RelayCommand RestoreSnapshotCommand { get; }
        public RelayCommand DeleteSnapshotCommand { get; }

        public SystemViewModel()
        {
            // Начальные значения внешнего вида — минуя публичные сеттеры (они
            // заблокированы _loadingAppearance) и без ProfileService.Save()/
            // ThemeService.Apply(), которые уместны только при реальном
            // пользовательском изменении, не при загрузке текущего состояния.
            _themeTag     = ProfileService.Current.Theme;
            _languageTag  = ProfileService.Current.Language;
            _compactMode  = ProfileService.Current.CompactMode;
            _reduceMotion = ProfileService.Current.ReduceMotion;
            MotionService.Enabled = !ProfileService.Current.ReduceMotion;
            _loadingAppearance = false;

            _minimizeToTray = ProfileService.Current.MinimizeToTray;

            CheckUpdatesCommand               = RelayCommand.FromAsync(_ => RunCheckUpdatesAsync(),    _ => !IsCheckingUpdates);
            BrowseDefaultInstallFolderCommand = new RelayCommand(_ => BrowseDefaultInstallFolder());
            UnhideAllAppsCommand              = new RelayCommand(_ => UnhideAllApps());
            ExportSettingsCommand             = new RelayCommand(_ => ExportSettings());
            ImportSettingsCommand             = new RelayCommand(_ => ImportSettings());
            SelectAllCacheCommand             = new RelayCommand(_ => SelectAllCache());
            SelectNoneCacheCommand            = new RelayCommand(_ => SelectNoneCache());
            BrowseCachePathCommand            = new RelayCommand(_ => BrowseCachePath());
            OpenCacheFolderCommand            = new RelayCommand(_ => OpenCacheFolder());
            ClearCacheCommand                 = new RelayCommand(_ => ClearCache());
            DownloadToCacheCommand            = RelayCommand.FromAsync(_ => RunDownloadToCacheAsync(), _ => !IsDownloadingToCache);
            CancelCacheDownloadCommand        = new RelayCommand(_ => CancelCacheDownload());
            MoveSourceUpCommand               = new RelayCommand(_ => MoveSourceUp());
            MoveSourceDownCommand             = new RelayCommand(_ => MoveSourceDown());
            SaveSourceOrderCommand            = new RelayCommand(_ => SaveSourceOrder());
            SaveSnapshotCommand               = RelayCommand.FromAsync(_ => RunSaveSnapshotAsync(),    _ => !IsSavingSnapshot);
            RestoreSnapshotCommand            = RelayCommand.FromAsync(p => RunRestoreSnapshotAsync(p as SnapshotRow), p => p is SnapshotRow row && !row.IsRestoring);
            DeleteSnapshotCommand             = new RelayCommand(p => DeleteSnapshot(p as SnapshotRow));

            LoadSettings();
            LoadOfflineSettings();
        }

        /// <summary>
        /// Вызывается из code-behind при первом Loaded (гейт _initialized остался
        /// в SystemTab.xaml.cs — WPF-lifecycle забота, не VM-концерн).
        /// </summary>
        public void Initialize()
        {
            LoadSourceOrderUI();
            UpdateCacheStats();
            LoadCacheAppsList();
            LoadSnapshotsList();
        }
    }
}
```

- [ ] **Step 5: Создать `Ven4Tools/ViewModels/SystemViewModel.Appearance.cs`**

```csharp
using Ven4Tools.Services;
using Ven4Tools.Shared;

namespace Ven4Tools.ViewModels
{
    public sealed partial class SystemViewModel
    {
        private string _themeTag = "web";
        public string ThemeTag
        {
            get => _themeTag;
            set
            {
                if (_loadingAppearance || _themeTag == value) return;
                SetField(ref _themeTag, value);
                ProfileService.Current.Theme = value;
                ProfileService.Save();
                ThemeService.Apply(value);
                ThemeApplied?.Invoke();
            }
        }

        private string _languageTag = "auto";
        public string LanguageTag
        {
            get => _languageTag;
            set
            {
                if (_loadingAppearance || _languageTag == value) return;
                SetField(ref _languageTag, value);
                ProfileService.Current.Language = value;
                ProfileService.Save();
                var language = value;
                if (language == "auto")
                    language = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
                LocalizationService.Apply(language);
            }
        }

        private bool _compactMode;
        public bool CompactMode
        {
            get => _compactMode;
            set
            {
                if (_loadingAppearance || _compactMode == value) return;
                SetField(ref _compactMode, value);
                ProfileService.Current.CompactMode = value;
                ProfileService.Save();
            }
        }

        private bool _reduceMotion;
        public bool ReduceMotion
        {
            get => _reduceMotion;
            set
            {
                if (_loadingAppearance || _reduceMotion == value) return;
                SetField(ref _reduceMotion, value);
                MotionService.Enabled = !value;
                ProfileService.Current.ReduceMotion = value;
                ProfileService.Save();
            }
        }

        // Без гейта _loadingAppearance — оригинальный ChkMinimizeToTray_Click тоже без него
        // (Click, в отличие от SelectionChanged, не срабатывает на программное присваивание).
        private bool _minimizeToTray;
        public bool MinimizeToTray
        {
            get => _minimizeToTray;
            set
            {
                if (_minimizeToTray == value) return;
                SetField(ref _minimizeToTray, value);
                ProfileService.Current.MinimizeToTray = value;
                ProfileService.Save();
            }
        }
    }
}
```

- [ ] **Step 6: Создать `Ven4Tools/ViewModels/SystemViewModel.Settings.cs`**

```csharp
using System;
using System.Windows;
using Microsoft.Win32;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class SystemViewModel
    {
        private void LoadSettings()
        {
            SetField(ref _notifyInstallComplete, ProfileService.Current.NotifyInstallComplete, nameof(NotifyInstallComplete));
            SetField(ref _notifyAppUpdates, ProfileService.Current.NotifyAppUpdates, nameof(NotifyAppUpdates));

            double catalogTimeout = Math.Clamp(AppSettings.CatalogTimeout, 3, 30);
            double checkTimeout   = Math.Clamp(AppSettings.CheckTimeout, 5, 60);
            SetField(ref _catalogTimeoutValue, catalogTimeout, nameof(CatalogTimeoutValue));
            SetField(ref _checkTimeoutValue, checkTimeout, nameof(CheckTimeoutValue));
            CatalogTimeoutText = $"{(int)catalogTimeout} сек";
            CheckTimeoutText   = $"{(int)checkTimeout} сек";

            SetField(ref _silentInstall, ProfileService.Current.SilentInstall, nameof(SilentInstall));
            SetField(ref _defaultInstallFolderText, ProfileService.Current.DefaultInstallFolder, nameof(DefaultInstallFolderText));
            DefaultInstallFolderStatusText = "";

            _loadingCatalogMode = true;
            SetField(ref _catalogModeTag, ProfileService.Current.CatalogMode, nameof(CatalogModeTag));
            _loadingCatalogMode = false;
        }

        private void SaveSettings()
        {
            AppSettings.Save(
                catalogTimeout: (int)CatalogTimeoutValue,
                checkTimeout:   (int)CheckTimeoutValue);

            ProfileService.Current.NotifyInstallComplete = NotifyInstallComplete;
            ProfileService.Current.NotifyAppUpdates = NotifyAppUpdates;
            ProfileService.Save();
        }

        // ── Уведомления / таймауты ───────────────────────────────────────────────

        private bool _notifyInstallComplete = true;
        public bool NotifyInstallComplete
        {
            get => _notifyInstallComplete;
            set
            {
                if (_notifyInstallComplete == value) return;
                SetField(ref _notifyInstallComplete, value);
                SaveSettings();
            }
        }

        private bool _notifyAppUpdates = true;
        public bool NotifyAppUpdates
        {
            get => _notifyAppUpdates;
            set
            {
                if (_notifyAppUpdates == value) return;
                SetField(ref _notifyAppUpdates, value);
                SaveSettings();
            }
        }

        private double _catalogTimeoutValue = 10;
        public double CatalogTimeoutValue
        {
            get => _catalogTimeoutValue;
            set
            {
                if (_catalogTimeoutValue == value) return;
                SetField(ref _catalogTimeoutValue, value);
                CatalogTimeoutText = $"{(int)value} сек";
                SaveSettings();
            }
        }

        private string _catalogTimeoutText = "10 сек";
        public string CatalogTimeoutText { get => _catalogTimeoutText; private set => SetField(ref _catalogTimeoutText, value); }

        private double _checkTimeoutValue = 15;
        public double CheckTimeoutValue
        {
            get => _checkTimeoutValue;
            set
            {
                if (_checkTimeoutValue == value) return;
                SetField(ref _checkTimeoutValue, value);
                CheckTimeoutText = $"{(int)value} сек";
                SaveSettings();
            }
        }

        private string _checkTimeoutText = "15 сек";
        public string CheckTimeoutText { get => _checkTimeoutText; private set => SetField(ref _checkTimeoutText, value); }

        // ── Установка приложений ──────────────────────────────────────────────────

        private bool _silentInstall;
        public bool SilentInstall
        {
            get => _silentInstall;
            set
            {
                if (_silentInstall == value) return;
                SetField(ref _silentInstall, value);
                ProfileService.Current.SilentInstall = value;
                ProfileService.Save();
            }
        }

        private string _defaultInstallFolderText = "";
        public string DefaultInstallFolderText
        {
            get => _defaultInstallFolderText;
            set => ApplyDefaultInstallFolder(value);
        }

        private string _defaultInstallFolderStatusText = "";
        public string DefaultInstallFolderStatusText { get => _defaultInstallFolderStatusText; private set => SetField(ref _defaultInstallFolderStatusText, value); }

        private void BrowseDefaultInstallFolder()
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description         = "Выберите папку установки приложений по умолчанию",
                ShowNewFolderButton = true
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            ApplyDefaultInstallFolder(dlg.SelectedPath);
        }

        /// <summary>
        /// Сохраняет папку установки в профиль, предварительно прогоняя её через тот же
        /// CommandLineGuard.ValidateInstallFolder, которым пользуется путь winget. Иначе
        /// значение молча отбрасывалось бы только в момент установки, и пользователь
        /// считал бы, что папка задана. Пустая строка допустима — это штатный сброс
        /// к выбору winget по умолчанию.
        /// </summary>
        private void ApplyDefaultInstallFolder(string? path)
        {
            string value = (path ?? "").Trim();

            if (!CommandLineGuard.ValidateInstallFolder(value))
            {
                SetField(ref _defaultInstallFolderText, ProfileService.Current.DefaultInstallFolder, nameof(DefaultInstallFolderText));
                DefaultInstallFolderStatusText =
                    "⚠ Путь не принят: нужен абсолютный локальный путь без сетевых имён и кавычек. Оставлено прежнее значение.";
                return;
            }

            SetField(ref _defaultInstallFolderText, value, nameof(DefaultInstallFolderText));
            ProfileService.Current.DefaultInstallFolder = value;
            ProfileService.Save();

            DefaultInstallFolderStatusText = value.Length == 0
                ? "Папка не задана — winget выбирает её сам."
                : $"Сохранено: {value}";
        }

        // ── Область каталога ───────────────────────────────────────────────────────

        private string _catalogModeTag = "full";
        public string CatalogModeTag
        {
            get => _catalogModeTag;
            set
            {
                if (_loadingCatalogMode || _catalogModeTag == value) return;
                SetField(ref _catalogModeTag, value);
                ProfileService.Current.CatalogMode = value;
                ProfileService.Save();
            }
        }

        // ── Скрытые приложения ─────────────────────────────────────────────────────

        private string _hiddenAppsStatusText = "";
        public string HiddenAppsStatusText { get => _hiddenAppsStatusText; private set => SetField(ref _hiddenAppsStatusText, value); }

        private void UnhideAllApps()
        {
            // Отдельный экземпляр AppManager нарочно: он ничего не держит в памяти
            // кроме файлового состояния (apps.json/alternatives.json/hidden.json).
            var appManager = new AppManager();
            int count = appManager.HiddenAppsCount;
            if (count == 0)
            {
                HiddenAppsStatusText = "Скрытых приложений нет.";
                return;
            }

            appManager.UnhideAllApps();
            HiddenAppsStatusText =
                $"Показано: {count}. Чтобы увидеть их в списке — «Обновить каталог» на вкладке «Каталог» или перезапустите клиент.";
            AppLogger.Write($"👁 Показаны скрытые приложения ({count})");
        }

        // ── Перенос настроек (экспорт/импорт) ─────────────────────────────────────

        private string _transferStatusText = "";
        public string TransferStatusText { get => _transferStatusText; private set => SetField(ref _transferStatusText, value); }

        private void ExportSettings()
        {
            try
            {
                var dlg = new SaveFileDialog
                {
                    Title    = "Экспорт настроек Ven4Tools",
                    Filter   = "Архив настроек Ven4Tools (*.zip)|*.zip",
                    FileName = $"Ven4Tools-настройки-{DateTime.Now:yyyy-MM-dd}.zip"
                };
                if (dlg.ShowDialog() != true) return;

                var result = ProfileExportService.Export(dlg.FileName);
                TransferStatusText = result.Message;
                AppLogger.Write(result.Success ? $"📤 {result.Message}" : $"❌ {result.Message}");
                if (!result.Success)
                    MessageBox.Show(result.Message, "Экспорт настроек",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка экспорта настроек: {ex.Message}");
                MessageBox.Show($"Не удалось экспортировать настройки: {ex.Message}",
                    "Экспорт настроек", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImportSettings()
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title  = "Импорт настроек Ven4Tools",
                    Filter = "Архив настроек Ven4Tools (*.zip)|*.zip|Все файлы (*.*)|*.*"
                };
                if (dlg.ShowDialog() != true) return;

                var confirm = MessageBox.Show(
                    "Текущие локальные настройки (профиль, пресеты, избранное, параметры приложения) будут перезаписаны данными из архива.\n\nПродолжить?",
                    "Импорт настроек", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;

                var result = ProfileExportService.Import(dlg.FileName);
                TransferStatusText = result.Message;
                AppLogger.Write(result.Success ? $"📥 {result.Message}" : $"❌ {result.Message}");

                if (!result.Success)
                {
                    MessageBox.Show(result.Message, "Импорт настроек",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Обновляем состояние вкладки и оформление по свежим данным сервисов
                LoadSettings();
                LoadOfflineSettings();
                LoadSourceOrderUI();
                SetField(ref _minimizeToTray, ProfileService.Current.MinimizeToTray, nameof(MinimizeToTray));
                ThemeService.Apply(ProfileService.Current.Theme);
                ThemeApplied?.Invoke();
                LocalizationService.Init();

                MessageBox.Show(
                    result.Message + "\n\nНастройки применены. Избранное обновится после перезапуска приложения.",
                    "Импорт настроек", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка импорта настроек: {ex.Message}");
                MessageBox.Show($"Не удалось импортировать настройки: {ex.Message}",
                    "Импорт настроек", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
```

- [ ] **Step 7: Создать `Ven4Tools/ViewModels/SystemViewModel.AppUpdates.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class SystemViewModel
    {
        private bool _isCheckingUpdates;
        public bool IsCheckingUpdates
        {
            get => _isCheckingUpdates;
            private set { SetField(ref _isCheckingUpdates, value); CheckUpdatesCommand.RaiseCanExecuteChanged(); }
        }

        private string _updatesLogText = "Нажмите «Проверить обновления» для проверки...";
        public string UpdatesLogText { get => _updatesLogText; private set => SetField(ref _updatesLogText, value); }

        private async Task RunCheckUpdatesAsync()
        {
            if (IsCheckingUpdates) return;

            IsCheckingUpdates = true;
            UpdatesLogText = "⏳ Проверка...";
            try
            {
                var (_, raw) = await WingetRunner.RunAsync(
                    $"upgrade --include-unknown --source winget {WingetArgs.NonInteractiveLine}",
                    TimeSpan.FromMinutes(3));

                var upgradable = ParseUpgradableRows(raw);

                if (upgradable.Count > 0)
                {
                    UpdatesLogText = $"🔔 Доступно обновлений: {upgradable.Count}\n\n" + string.Join("\n", upgradable);
                    AppLogger.Write($"🔔 Доступно обновлений winget: {upgradable.Count}");
                }
                else
                {
                    UpdatesLogText = "✅ Все установленные приложения актуальны";
                    AppLogger.Write("✅ Обновлений winget не найдено");
                }
            }
            catch (Exception ex)
            {
                UpdatesLogText = $"❌ Ошибка: {ex.Message}";
                AppLogger.Write($"❌ Ошибка проверки обновлений: {ex.Message}");
            }
            finally
            {
                IsCheckingUpdates = false;
            }
        }

        // Разбор таблицы winget upgrade: строки между разделителем «---» и футером,
        // локаленезависимый критерий (WingetRunner.IsTableSeparator/IsTableRow —
        // внутренний разрыв в 2+ пробела = строка таблицы), не английские префиксы —
        // проект принципиально не передаёт winget --locale en-US, и на русской Windows
        // такие префиксы не совпадали, из-за чего заголовок и футер попадали в список
        // «доступных обновлений», завышая счётчик.
        internal static List<string> ParseUpgradableRows(string raw)
        {
            var rows = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return rows;

            var lines = WingetRunner.StripAnsi(raw).Replace("\r", "").Split('\n');
            int sepIdx = Array.FindIndex(lines, WingetRunner.IsTableSeparator);
            if (sepIdx < 0) return rows;

            for (int i = sepIdx + 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) break;
                if (WingetRunner.IsTableSeparator(line)) continue;
                if (!WingetRunner.IsTableRow(line)) break;
                rows.Add(line.Trim());
            }
            return rows;
        }
    }
}
```

- [ ] **Step 8: Создать `Ven4Tools/ViewModels/SystemViewModel.Offline.cs`**

```csharp
using System.Windows.Media;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class SystemViewModel
    {
        private void LoadOfflineSettings()
        {
            SetField(ref _offlineMode, ProfileService.Current.OfflineMode, nameof(OfflineMode));
            SetField(ref _forceOnlineMode, ProfileService.Current.ForceOnlineMode, nameof(ForceOnlineMode));
            SetField(ref _paranoidMode, ProfileService.Current.ParanoidMode, nameof(ParanoidMode));

            string cachePath = ProfileService.Current.OfflineCachePath;
            if (string.IsNullOrEmpty(cachePath)) cachePath = OfflineService.CacheBasePath;
            SetField(ref _offlineCachePathText, cachePath, nameof(OfflineCachePathText));
        }

        private void SaveOfflineSettings()
        {
            ProfileService.Current.OfflineCachePath = OfflineCachePathText.Trim();
            ProfileService.Save();
        }

        private bool _offlineMode;
        public bool OfflineMode
        {
            get => _offlineMode;
            set
            {
                if (_offlineMode == value) return;
                SetField(ref _offlineMode, value);
                ProfileService.Current.OfflineMode = value;
                ProfileService.Save();
                RefreshTabVisibility?.Invoke();
                UpdateConnectivityStatus();
            }
        }

        private bool _forceOnlineMode;
        public bool ForceOnlineMode
        {
            get => _forceOnlineMode;
            set
            {
                if (_forceOnlineMode == value) return;
                SetField(ref _forceOnlineMode, value);
                ProfileService.Current.ForceOnlineMode = value;
                ProfileService.Save();
                RefreshTabVisibility?.Invoke();
                UpdateConnectivityStatus();
            }
        }

        private bool _paranoidMode;
        public bool ParanoidMode
        {
            get => _paranoidMode;
            set
            {
                if (_paranoidMode == value) return;
                SetField(ref _paranoidMode, value);
                ProfileService.Current.ParanoidMode = value;
                ProfileService.Save();
            }
        }

        // Сохранение — только по LostFocus (см. UpdateSourceTrigger=LostFocus в XAML),
        // не на каждое нажатие клавиши, ровно как в оригинале (txtOfflineCachePath.LostFocus).
        private string _offlineCachePathText = "";
        public string OfflineCachePathText
        {
            get => _offlineCachePathText;
            set
            {
                if (_offlineCachePathText == value) return;
                SetField(ref _offlineCachePathText, value);
                SaveOfflineSettings();
            }
        }

        private string _connIconText = "🟢";
        public string ConnIconText { get => _connIconText; private set => SetField(ref _connIconText, value); }

        private string _connStatusText = "Интернет доступен";
        public string ConnStatusText { get => _connStatusText; private set => SetField(ref _connStatusText, value); }

        // Дефолт — прозрачная кисть: в оригинальном XAML у pnlConnStatus нет статичного
        // Background, цвет всегда выставлялся программно из UpdateConnectivityStatus().
        private Brush _connStatusBackground = Brushes.Transparent;
        public Brush ConnStatusBackground { get => _connStatusBackground; private set => SetField(ref _connStatusBackground, value); }

        public void UpdateConnectivityStatus()
        {
            bool online        = ConnectivityMonitor.IsOnline;
            bool offlineForced = ProfileService.Current.OfflineMode;
            bool onlineForced  = ProfileService.Current.ForceOnlineMode;

            if (offlineForced)
            {
                ConnIconText = "🟡";
                ConnStatusText = "Принудительный офлайн — вкладки скрыты вручную";
                ConnStatusBackground = new SolidColorBrush(Color.FromRgb(70, 55, 10));
            }
            else if (!online && onlineForced)
            {
                ConnIconText = "🟠";
                ConnStatusText = "Соединение не обнаружено, но онлайн-режим принудительно включён";
                ConnStatusBackground = new SolidColorBrush(Color.FromRgb(80, 45, 5));
            }
            else if (!online)
            {
                ConnIconText = "🔴";
                ConnStatusText = "Интернет недоступен — онлайн-вкладки скрыты";
                ConnStatusBackground = new SolidColorBrush(Color.FromRgb(80, 20, 20));
            }
            else
            {
                ConnIconText = "🟢";
                ConnStatusText = "Интернет доступен — все вкладки активны";
                ConnStatusBackground = new SolidColorBrush(Color.FromRgb(15, 50, 20));
            }
            ConnectivityStatusUpdated?.Invoke();
        }
    }
}
```

- [ ] **Step 9: Создать `Ven4Tools/ViewModels/SystemViewModel.Cache.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class SystemViewModel
    {
        // Единый HttpClient для скачивания установщиков в кэш — переиспользуется,
        // чтобы не плодить сокеты (socket exhaustion) при каждом запуске загрузки.
        private static readonly HttpClient _httpClient = CreateCacheHttpClient();

        private static HttpClient CreateCacheHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools");
            return client;
        }

        private CancellationTokenSource? _cacheCts;
        private List<CacheAppItem> _cacheAppItems = new();

        private string _cacheStatsText = "Кэш пуст";
        public string CacheStatsText { get => _cacheStatsText; private set => SetField(ref _cacheStatsText, value); }

        private IReadOnlyList<CacheAppItem> _filteredCacheApps = Array.Empty<CacheAppItem>();
        public IReadOnlyList<CacheAppItem> FilteredCacheApps { get => _filteredCacheApps; private set => SetField(ref _filteredCacheApps, value); }

        private string _cacheAppFilterText = "";
        public string CacheAppFilterText
        {
            get => _cacheAppFilterText;
            set
            {
                if (_cacheAppFilterText == value) return;
                SetField(ref _cacheAppFilterText, value);
                ApplyCacheAppFilter();
            }
        }

        private void ApplyCacheAppFilter()
        {
            string q = CacheAppFilterText.Trim().ToLowerInvariant();
            FilteredCacheApps = string.IsNullOrEmpty(q)
                ? _cacheAppItems
                : _cacheAppItems.Where(a => a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private void UpdateCacheStats()
        {
            var (count, sizeMB) = OfflineService.GetCacheStats();
            CacheStatsText = count == 0
                ? "Кэш пуст"
                : $"{count} файлов · {sizeMB} МБ  ({OfflineService.CachePath})";
        }

        private void LoadCacheAppsList()
        {
            // UsableCatalog отдаёт каталог только со статусом Loaded — прежняя проверка
            // «null или пусто» теперь выражена самим состоянием загрузки.
            var catalog = CatalogLoaderService.State.UsableCatalog;
            if (catalog == null)
            {
                _cacheAppItems = new List<CacheAppItem>();
                FilteredCacheApps = _cacheAppItems;
                return;
            }

            // Кэшируются только приложения с прямой ссылкой и контрольной суммой SHA256.
            // Источник winget не поддерживает докачивание установщика в кэш, поэтому
            // winget-only приложения в этот список не попадают.
            _cacheAppItems = catalog.Apps
                .Where(a => HashHelper.HasExpectedHash(a.Sha256) &&
                            !string.IsNullOrEmpty(a.DownloadUrl))
                .OrderBy(a => a.Name)
                .Select(a => new CacheAppItem
                {
                    Id          = a.Id,
                    DisplayName = $"{a.Name}  [{a.Category}]{(OfflineService.HasCachedInstaller(a.Id) ? " ✅" : "")}",
                    DownloadUrl = a.DownloadUrl,
                    Sha256      = a.Sha256!
                })
                .ToList();

            ApplyCacheAppFilter();
        }

        private void SelectAllCache()
        {
            // L12: выбираем только видимые (не отфильтрованные поиском) элементы, а не весь
            // список — иначе «Выбрать все» тихо отмечало бы и скрытые фильтром приложения.
            foreach (var item in FilteredCacheApps) item.IsSelected = true;
        }

        private void SelectNoneCache()
        {
            foreach (var item in _cacheAppItems) item.IsSelected = false;
        }

        private void BrowseCachePath()
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description         = "Выберите папку для кэша установщиков",
                ShowNewFolderButton = true
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            OfflineCachePathText = dlg.SelectedPath;
            UpdateCacheStats();
        }

        private void OpenCacheFolder()
        {
            try
            {
                OfflineService.EnsureCacheDir();
                Process.Start(new ProcessStartInfo(OfflineService.CachePath) { UseShellExecute = true });
            }
            catch (Exception ex) { AppLogger.Write($"❌ {ex.Message}"); }
        }

        private void ClearCache()
        {
            var r = MessageBox.Show("Удалить все кэшированные установщики?",
                "Очистка кэша", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            OfflineService.ClearCache();
            UpdateCacheStats();
            LoadCacheAppsList();
            AppLogger.Write("✅ Кэш очищен");
        }

        private bool _isDownloadingToCache;
        public bool IsDownloadingToCache
        {
            get => _isDownloadingToCache;
            private set { SetField(ref _isDownloadingToCache, value); DownloadToCacheCommand.RaiseCanExecuteChanged(); }
        }

        private string _cacheLogText = "";
        public string CacheLogText { get => _cacheLogText; private set => SetField(ref _cacheLogText, value); }

        private void AppendCacheLog(string line)
        {
            CacheLogText += line;
            CacheLogAppended?.Invoke();
        }

        private double _cacheProgressValue;
        public double CacheProgressValue { get => _cacheProgressValue; private set => SetField(ref _cacheProgressValue, value); }

        private bool _showCacheProgress;
        public bool ShowCacheProgress { get => _showCacheProgress; private set => SetField(ref _showCacheProgress, value); }

        private bool _showCacheLog;
        public bool ShowCacheLog { get => _showCacheLog; private set => SetField(ref _showCacheLog, value); }

        private bool _showCancelCacheDownload;
        public bool ShowCancelCacheDownload { get => _showCancelCacheDownload; private set => SetField(ref _showCancelCacheDownload, value); }

        private bool _canCancelCacheDownload = true;
        public bool CanCancelCacheDownload { get => _canCancelCacheDownload; private set => SetField(ref _canCancelCacheDownload, value); }

        private async Task RunDownloadToCacheAsync()
        {
            if (IsDownloadingToCache) return;

            var selected = _cacheAppItems.Where(a => a.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Не выбрано ни одного приложения.", "Нет выбора",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            IsDownloadingToCache = true;
            _cacheCts = new CancellationTokenSource();
            var token = _cacheCts.Token;

            ShowCancelCacheDownload = true;
            CanCancelCacheDownload  = true;
            ShowCacheProgress       = true;
            ShowCacheLog            = true;
            CacheLogText            = "";

            // Вся подготовка — внутри try: исключение здесь (например, недопустимый путь
            // кэша в EnsureCacheDir) не должно ронять команду мимо finally.
            try
            {
                SaveOfflineSettings();
                OfflineService.EnsureCacheDir();

                var http = _httpClient;
                int done = 0, total = selected.Count, errors = 0;

                foreach (var item in selected)
                {
                    if (token.IsCancellationRequested) break;

                    // Ven4Tools.Models.App — полное имя, чтобы не столкнуться с
                    // System.Windows.Application (using System.Windows уже есть в проекте).
                    var app = new Ven4Tools.Models.App
                    {
                        Id          = item.Id,
                        Name        = item.DisplayName.Split('[')[0].Trim().TrimEnd(' ', '✅').Trim(),
                        DownloadUrl = item.DownloadUrl,
                        Sha256      = item.Sha256
                    };

                    var progress = new Progress<(string status, int pct)>(v =>
                    {
                        if (v.pct >= 0) CacheProgressValue = v.pct;
                        AppendCacheLog($"[{DateTime.Now:HH:mm:ss}] {v.status}\n");
                    });

                    try
                    {
                        bool ok = await OfflineService.CacheInstallerDirectAsync(app, http, progress, token);
                        if (!ok) errors++;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        AppendCacheLog($"❌ {app.Name}: {ex.Message}\n");
                        errors++;
                    }

                    done++;
                    CacheProgressValue = (double)done / total * 100;
                }

                string summary = token.IsCancellationRequested
                    ? $"⏹ Остановлено. Скачано: {done}/{total}"
                    : $"✅ Готово: {done}/{total}{(errors > 0 ? $", ошибок: {errors}" : "")}";
                AppendCacheLog($"\n{summary}\n");
                AppLogger.Write(summary);
            }
            catch (Exception ex)
            {
                AppendCacheLog($"❌ Ошибка: {ex.Message}\n");
                AppLogger.Write($"❌ Ошибка кэширования: {ex.Message}");
            }
            finally
            {
                IsDownloadingToCache    = false;
                ShowCancelCacheDownload = false;
                CanCancelCacheDownload  = true;
                CacheProgressValue      = 0;
                UpdateCacheStats();
                LoadCacheAppsList();

                _cacheCts.Dispose();
                _cacheCts = null;
            }
        }

        private void CancelCacheDownload()
        {
            _cacheCts?.Cancel();
            CanCancelCacheDownload = false;
        }
    }
}
```

- [ ] **Step 10: Создать `Ven4Tools/ViewModels/SystemViewModel.Sources.cs`**

```csharp
using System.Collections.ObjectModel;
using System.Linq;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class SystemViewModel
    {
        public ObservableCollection<SourceItem> SourceItems { get; } = new();

        private int _selectedSourceIndex = -1;
        public int SelectedSourceIndex { get => _selectedSourceIndex; set => SetField(ref _selectedSourceIndex, value); }

        // Урок InstalledTab (SetFilterFlag): TwoWay-запись группы RadioButton идёт в
        // порядке, обратном событию Checked — сеттер новой выбранной кнопки получает
        // true ПЕРВЫМ, сосед сбрасывается в false ВТОРЫМ. Оба сеттера ниже безусловно
        // (не только при value==true) вызывают UpdateSourcePanels() — один транзиентный
        // лишний пересчёт, но финальное состояние после второй записи всегда корректно.
        private bool _isGlobalSourceMode = true;
        public bool IsGlobalSourceMode
        {
            get => _isGlobalSourceMode;
            set
            {
                if (_isGlobalSourceMode == value) return;
                SetField(ref _isGlobalSourceMode, value);
                UpdateSourcePanels();
            }
        }

        private bool _isPerCategorySourceMode;
        public bool IsPerCategorySourceMode
        {
            get => _isPerCategorySourceMode;
            set
            {
                if (_isPerCategorySourceMode == value) return;
                SetField(ref _isPerCategorySourceMode, value);
                UpdateSourcePanels();
            }
        }

        private bool _showGlobalOrderPanel = true;
        public bool ShowGlobalOrderPanel { get => _showGlobalOrderPanel; private set => SetField(ref _showGlobalOrderPanel, value); }

        private bool _showPerCategoryHint;
        public bool ShowPerCategoryHint { get => _showPerCategoryHint; private set => SetField(ref _showPerCategoryHint, value); }

        private string _sourceOrderStatusText = "";
        public string SourceOrderStatusText { get => _sourceOrderStatusText; private set => SetField(ref _sourceOrderStatusText, value); }

        private void LoadSourceOrderUI()
        {
            var settings = SourceOrderService.Current;
            SetField(ref _isGlobalSourceMode, settings.Mode == "global", nameof(IsGlobalSourceMode));
            SetField(ref _isPerCategorySourceMode, settings.Mode == "per_category", nameof(IsPerCategorySourceMode));

            SourceItems.Clear();
            foreach (var id in settings.GlobalOrder)
                SourceItems.Add(new SourceItem { Id = id, Label = SourceOrderSettings.Labels.GetValueOrDefault(id, id) });

            UpdateSourcePanels();
        }

        private void UpdateSourcePanels()
        {
            ShowGlobalOrderPanel = IsGlobalSourceMode;
            ShowPerCategoryHint  = !IsGlobalSourceMode;
        }

        private void MoveSourceUp()
        {
            int idx = SelectedSourceIndex;
            if (idx <= 0) return;
            SourceItems.Move(idx, idx - 1);
            SelectedSourceIndex = idx - 1;
        }

        private void MoveSourceDown()
        {
            int idx = SelectedSourceIndex;
            if (idx < 0 || idx >= SourceItems.Count - 1) return;
            SourceItems.Move(idx, idx + 1);
            SelectedSourceIndex = idx + 1;
        }

        private void SaveSourceOrder()
        {
            SourceOrderService.Current.Mode        = IsGlobalSourceMode ? "global" : "per_category";
            SourceOrderService.Current.GlobalOrder = SourceItems.Select(i => i.Id).ToList();
            SourceOrderService.Save();

            SourceOrderStatusText = $"✅ Сохранено {System.DateTime.Now:HH:mm:ss} — изменится при следующем открытии каталога";
            AppLogger.Write("🔀 Порядок источников сохранён");
        }
    }
}
```

- [ ] **Step 11: Создать `Ven4Tools/ViewModels/SystemViewModel.Snapshots.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;
using Ven4Tools.Views;

namespace Ven4Tools.ViewModels
{
    public sealed partial class SystemViewModel
    {
        public ObservableCollection<SnapshotRow> Snapshots { get; } = new();

        private bool _showSnapshotsEmpty = true;
        public bool ShowSnapshotsEmpty { get => _showSnapshotsEmpty; private set => SetField(ref _showSnapshotsEmpty, value); }

        private string _snapshotStatusText = "";
        public string SnapshotStatusText { get => _snapshotStatusText; private set => SetField(ref _snapshotStatusText, value); }

        private bool _isSavingSnapshot;
        public bool IsSavingSnapshot
        {
            get => _isSavingSnapshot;
            private set { SetField(ref _isSavingSnapshot, value); SaveSnapshotCommand.RaiseCanExecuteChanged(); }
        }

        private void LoadSnapshotsList()
        {
            Snapshots.Clear();
            foreach (var s in ConfigSnapshotService.GetSnapshots())
                Snapshots.Add(new SnapshotRow(s));

            ShowSnapshotsEmpty = Snapshots.Count == 0;
        }

        private async Task RunSaveSnapshotAsync()
        {
            if (IsSavingSnapshot) return;

            var debloaterTab = DebloaterTabProvider?.Invoke();
            var tweakIds = debloaterTab?.GetSelectedTweakIds() ?? new List<string>();
            var presets = await PresetService.LoadAsync();

            var dlg = new SnapshotNameDialog(tweakIds.Count, presets.Count) { Owner = OwnerWindowProvider?.Invoke() };
            if (dlg.ShowDialog() != true) return;

            IsSavingSnapshot = true;
            try
            {
                string? path = await ConfigSnapshotService.SaveAsync(dlg.SnapshotName, tweakIds);
                SnapshotStatusText = path != null
                    ? $"✅ Снапшот «{dlg.SnapshotName}» сохранён {DateTime.Now:HH:mm:ss}"
                    : "❌ Не удалось сохранить снапшот";
                LoadSnapshotsList();
            }
            finally { IsSavingSnapshot = false; }
        }

        private async Task RunRestoreSnapshotAsync(SnapshotRow? row)
        {
            if (row == null || row.IsRestoring) return;

            var snapshot = ConfigSnapshotService.Load(row.Info.FilePath);
            if (snapshot == null)
            {
                MessageBox.Show("Не удалось прочитать файл снапшота — он повреждён или несовместим.",
                    "Снапшоты", MessageBoxButton.OK, MessageBoxImage.Error);
                LoadSnapshotsList();
                return;
            }

            row.IsRestoring = true;
            try
            {
                var debloaterTab = DebloaterTabProvider?.Invoke();
                int succeeded = 0, total = 0;
                if (debloaterTab != null && snapshot.DebloatTweakIds.Count > 0)
                {
                    // Единый диалог: подтверждение восстановления (Отмена = прервать) +
                    // предложение точки восстановления. Восстановление твиков делает те же
                    // необратимые системные изменения (реестр/службы/удаление Appx), что и
                    // «Применить» на вкладке «Очистка», поэтому точка восстановления нужна
                    // здесь по той же причине.
                    var rpOutcome = await UiGuards.ConfirmAndCreateRestorePointAsync(
                        $"Восстановить состояние из снапшота «{snapshot.Name}»?\n\n" +
                        $"Будет применено твиков: {snapshot.DebloatTweakIds.Count} (реестр/службы/удаление приложений, как на вкладке «Очистка»).\n" +
                        $"Локальные пресеты будут заменены содержимым снапшота ({snapshot.Presets.Count} шт.).\n\n" +
                        "Создать точку восстановления Windows перед восстановлением снапшота?",
                        "Ven4Tools — перед восстановлением снапшота");
                    if (rpOutcome == RestorePointOutcome.Cancelled)
                    {
                        SnapshotStatusText = "Отменено";
                        return;
                    }

                    var progress = new Progress<string>(name => SnapshotStatusText = $"⚙️ {name}...");
                    (succeeded, total) = await debloaterTab.ApplyTweaksByIdsAsync(snapshot.DebloatTweakIds, progress);
                    debloaterTab.SetSelectedTweakIds(snapshot.DebloatTweakIds);
                }
                else
                {
                    // Твиков нет — меняются только локальные пресеты, точка восстановления
                    // не относится к делу. Одно подтверждение действия.
                    var confirm = MessageBox.Show(
                        $"Восстановить состояние из снапшота «{snapshot.Name}»?\n\n" +
                        $"Локальные пресеты будут заменены содержимым снапшота ({snapshot.Presets.Count} шт.).",
                        "Снапшоты — подтверждение восстановления",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (confirm != MessageBoxResult.Yes) return;

                    SnapshotStatusText = "⏳ Восстанавливаю снапшот...";
                }

                bool presetsOk = await ConfigSnapshotService.RestorePresetsAsync(snapshot);

                SnapshotStatusText =
                    $"✅ Восстановлено {DateTime.Now:HH:mm:ss}: твиков {succeeded}/{total}" +
                    (presetsOk ? $", пресетов {snapshot.Presets.Count}" : ", ошибка восстановления пресетов");
                AppLogger.Write($"📸 Снапшот «{snapshot.Name}» восстановлен: твиков {succeeded}/{total}, пресетов {snapshot.Presets.Count}");
            }
            catch (Exception ex)
            {
                SnapshotStatusText = $"❌ Ошибка восстановления: {ex.Message}";
                AppLogger.Write($"[Снапшоты] Ошибка восстановления: {ex.Message}");
            }
            finally { row.IsRestoring = false; }
        }

        private void DeleteSnapshot(SnapshotRow? row)
        {
            if (row == null) return;

            var r = MessageBox.Show($"Удалить снапшот «{row.Info.Name}»?",
                "Снапшоты", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            if (ConfigSnapshotService.Delete(row.Info.FilePath))
            {
                Snapshots.Remove(row);
                ShowSnapshotsEmpty = Snapshots.Count == 0;
                AppLogger.Write($"🗑️ Снапшот «{row.Info.Name}» удалён");
            }
        }
    }
}
```

- [ ] **Step 12: Написать `tests/Ven4Tools.Tests/SystemViewModelTests.cs`**

```csharp
using Ven4Tools.Models;
using Ven4Tools.ViewModels;
using Xunit;

namespace Ven4Tools.Tests
{
    public class SystemViewModelTests
    {
        [Fact]
        public void Конструктор_УстанавливаетДефолтыБизиФлагов()
        {
            var vm = new SystemViewModel();

            Assert.False(vm.IsCheckingUpdates);
            Assert.False(vm.IsDownloadingToCache);
            Assert.False(vm.IsSavingSnapshot);
        }

        [Fact]
        public void Конструктор_УстанавливаетДефолтыНастроек()
        {
            var vm = new SystemViewModel();

            Assert.Equal("10 сек", vm.CatalogTimeoutText);
            Assert.Equal("15 сек", vm.CheckTimeoutText);
            Assert.Equal("", vm.DefaultInstallFolderStatusText);
            Assert.Equal("", vm.HiddenAppsStatusText);
            Assert.Equal("", vm.TransferStatusText);
        }

        [Fact]
        public void Конструктор_УстанавливаетДефолтОбновленийПриложений()
        {
            var vm = new SystemViewModel();

            Assert.Equal("Нажмите «Проверить обновления» для проверки...", vm.UpdatesLogText);
        }

        [Fact]
        public void Конструктор_УстанавливаетДефолтыКэша()
        {
            var vm = new SystemViewModel();

            Assert.Equal("Кэш пуст", vm.CacheStatsText);
            Assert.Empty(vm.FilteredCacheApps);
            Assert.False(vm.ShowCacheProgress);
            Assert.False(vm.ShowCacheLog);
            Assert.False(vm.ShowCancelCacheDownload);
            Assert.True(vm.CanCancelCacheDownload);
        }

        [Fact]
        public void Конструктор_УстанавливаетДефолтыИсточников()
        {
            var vm = new SystemViewModel();

            Assert.True(vm.IsGlobalSourceMode);
            Assert.False(vm.IsPerCategorySourceMode);
            Assert.True(vm.ShowGlobalOrderPanel);
            Assert.False(vm.ShowPerCategoryHint);
            Assert.Empty(vm.SourceItems);
        }

        [Fact]
        public void Конструктор_УстанавливаетДефолтыСнапшотов()
        {
            var vm = new SystemViewModel();

            Assert.Empty(vm.Snapshots);
            Assert.True(vm.ShowSnapshotsEmpty);
            Assert.Equal("", vm.SnapshotStatusText);
        }

        [Fact]
        public void КомандыСCanExecute_ИзначальноTrue()
        {
            var vm = new SystemViewModel();

            Assert.True(vm.CheckUpdatesCommand.CanExecute(null));
            Assert.True(vm.DownloadToCacheCommand.CanExecute(null));
            Assert.True(vm.SaveSnapshotCommand.CanExecute(null));
        }

        [Fact]
        public void КомандыБезCanExecute_ИзначальноTrue()
        {
            var vm = new SystemViewModel();

            Assert.True(vm.BrowseDefaultInstallFolderCommand.CanExecute(null));
            Assert.True(vm.UnhideAllAppsCommand.CanExecute(null));
            Assert.True(vm.ExportSettingsCommand.CanExecute(null));
            Assert.True(vm.ImportSettingsCommand.CanExecute(null));
            Assert.True(vm.SelectAllCacheCommand.CanExecute(null));
            Assert.True(vm.SelectNoneCacheCommand.CanExecute(null));
            Assert.True(vm.BrowseCachePathCommand.CanExecute(null));
            Assert.True(vm.OpenCacheFolderCommand.CanExecute(null));
            Assert.True(vm.ClearCacheCommand.CanExecute(null));
            Assert.True(vm.CancelCacheDownloadCommand.CanExecute(null));
            Assert.True(vm.MoveSourceUpCommand.CanExecute(null));
            Assert.True(vm.MoveSourceDownCommand.CanExecute(null));
            Assert.True(vm.SaveSourceOrderCommand.CanExecute(null));
        }

        [Fact]
        public void RestoreSnapshotCommand_CanExecute_ТребуетSnapshotRowИНеЗанятость()
        {
            var vm = new SystemViewModel();
            var row = new SnapshotRow(new ConfigSnapshotInfo { FilePath = "x", Name = "n" });

            Assert.False(vm.RestoreSnapshotCommand.CanExecute(null));
            Assert.True(vm.RestoreSnapshotCommand.CanExecute(row));

            row.IsRestoring = true;
            Assert.False(vm.RestoreSnapshotCommand.CanExecute(row));
        }

        [Fact]
        public void IsGlobalSourceMode_ИзменениеВFalse_БезусловноПересчитываетПанели()
        {
            var vm = new SystemViewModel();

            vm.IsGlobalSourceMode = false;

            Assert.False(vm.ShowGlobalOrderPanel);
            Assert.True(vm.ShowPerCategoryHint);
        }

        [Fact]
        public void IsPerCategorySourceMode_ИзменениеВTrue_БезусловноПересчитываетПанели()
        {
            var vm = new SystemViewModel();

            vm.IsPerCategorySourceMode = true;

            Assert.False(vm.ShowGlobalOrderPanel);
            Assert.True(vm.ShowPerCategoryHint);
        }

        [Fact]
        public void CacheAppItem_IsSelected_ПоднимаетPropertyChanged()
        {
            var item = new CacheAppItem { Id = "1", DisplayName = "App", DownloadUrl = "http://x", Sha256 = "" };
            bool raised = false;
            item.PropertyChanged += (_, e) => raised = e.PropertyName == nameof(CacheAppItem.IsSelected);

            item.IsSelected = true;

            Assert.True(raised);
        }

        [Fact]
        public void SnapshotRow_DisplayLabel_ПробрасываетИзInfo()
        {
            var info = new ConfigSnapshotInfo { Name = "Тест", TweakCount = 3, PresetCount = 2 };
            var row = new SnapshotRow(info);

            Assert.Equal(info.DisplayLabel, row.DisplayLabel);
        }

        [Fact]
        public void ParseUpgradableRows_РазбираетТаблицуИОтсекаетФутер()
        {
            string raw =
                "Имя              ИД             Версия   Доступно  Источник\n" +
                "----------------- --------------- -------- --------- --------\n" +
                "7-Zip              7zip.7zip       21.07    23.01     winget\n" +
                "Google Chrome      Google.Chrome   119.0    120.0     winget\n" +
                "\n" +
                "Доступны обновления: 2.\n";

            var rows = SystemViewModel.ParseUpgradableRows(raw);

            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.Contains("7-Zip"));
            Assert.Contains(rows, r => r.Contains("Google Chrome"));
            Assert.DoesNotContain(rows, r => r.Contains("Доступны обновления"));
        }

        [Fact]
        public void ParseUpgradableRows_БезРазделителя_ВозвращаетПусто()
        {
            var rows = SystemViewModel.ParseUpgradableRows("Просто текст без таблицы winget.");
            Assert.Empty(rows);
        }

        [Fact]
        public void ParseUpgradableRows_ПустаяИНеопределённаяСтрока_ВозвращаетПусто()
        {
            Assert.Empty(SystemViewModel.ParseUpgradableRows(""));
            Assert.Empty(SystemViewModel.ParseUpgradableRows(null!));
        }
    }
}
```

- [ ] **Step 13: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок, 0 предупреждений.

- [ ] **Step 14: Прогнать новые тесты**

Run: `dotnet test tests/Ven4Tools.Tests -c Release --filter "FullyQualifiedName~SystemViewModelTests"`
Expected: все новые тесты зелёные.

- [ ] **Step 15: Commit**

```bash
git add Ven4Tools/ViewModels/CacheAppItem.cs Ven4Tools/ViewModels/SourceItem.cs Ven4Tools/ViewModels/SnapshotRow.cs Ven4Tools/ViewModels/SystemViewModel.cs Ven4Tools/ViewModels/SystemViewModel.Appearance.cs Ven4Tools/ViewModels/SystemViewModel.Settings.cs Ven4Tools/ViewModels/SystemViewModel.AppUpdates.cs Ven4Tools/ViewModels/SystemViewModel.Offline.cs Ven4Tools/ViewModels/SystemViewModel.Cache.cs Ven4Tools/ViewModels/SystemViewModel.Sources.cs Ven4Tools/ViewModels/SystemViewModel.Snapshots.cs tests/Ven4Tools.Tests/SystemViewModelTests.cs
git commit -m "feat(system): SystemViewModel (11 файлов) + юнит-тесты"
```

---

### Task 2: Переписать `SystemTab.xaml`/`SystemTab.xaml.cs` на тонкую обёртку

**Files:**
- Modify: `Ven4Tools/Views/Tabs/SystemTab.xaml`
- Modify: `Ven4Tools/Views/Tabs/SystemTab.xaml.cs`
- Delete: `Ven4Tools/Views/Tabs/SystemTab.Appearance.cs`
- Delete: `Ven4Tools/Views/Tabs/SystemTab.Settings.cs`
- Delete: `Ven4Tools/Views/Tabs/SystemTab.AppUpdates.cs`
- Delete: `Ven4Tools/Views/Tabs/SystemTab.Offline.cs`
- Delete: `Ven4Tools/Views/Tabs/SystemTab.Cache.cs`
- Delete: `Ven4Tools/Views/Tabs/SystemTab.Sources.cs`
- Delete: `Ven4Tools/Views/Tabs/SystemTab.Snapshots.cs`

**Interfaces:**
- Consumes: `Ven4Tools.ViewModels.SystemViewModel` (Task 1) — вся публичная поверхность; `Ven4Tools.MainWindow.EnsureDebloaterTab()`/`UpdateTabVisibility()` (уже публичны); `Ven4Tools.Shared.MotionService`; `Ven4Tools.Services.ConnectivityMonitor`.
- Produces: `SystemTab` — публичной поверхности сверх конструктора нет (в отличие от Office/Diagnostics, здесь MainWindow не подписывается ни на какие события SystemTab).

- [ ] **Step 1: Переписать `Ven4Tools/Views/Tabs/SystemTab.xaml`**

Полное содержимое файла:

```xml
<UserControl x:Class="Ven4Tools.Views.Tabs.SystemTab"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="{DynamicResource ContentBackground}">
    <UserControl.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis"/>
    </UserControl.Resources>
    <Grid Margin="20">
        <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
        <StackPanel Margin="0,0,0,16">
            <TextBlock Text="Настройки" Style="{StaticResource PageTitleStyle}"/>
            <TextBlock Text="Интерфейс, источники, автономная работа и конфигурация"
                       Foreground="{DynamicResource TextSecondary}" Margin="0,4,0,0"/>
        </StackPanel>
        <TabControl Grid.Row="1">
            <TabItem Header="Общие">
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                    <StackPanel Margin="8,12,8,8">
            <GroupBox Header="Внешний вид" Margin="0,0,0,15">
                <Grid Margin="8">
                    <Grid.ColumnDefinitions><ColumnDefinition Width="150"/><ColumnDefinition Width="230"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                    <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
                    <TextBlock Text="Тема" VerticalAlignment="Center"/>
                    <ComboBox x:Name="cmbTheme" Grid.Column="1" SelectedValuePath="Tag" SelectedValue="{Binding ThemeTag, Mode=TwoWay}">
                        <ComboBoxItem Content="Как на ven4tools.ru" Tag="web"/>
                        <ComboBoxItem Content="Бирюзовая" Tag="teal"/>
                        <ComboBoxItem Content="Тёмная" Tag="dark"/>
                        <ComboBoxItem Content="Светлая" Tag="light"/>
                    </ComboBox>
                    <TextBlock Grid.Row="1" Text="Язык интерфейса" VerticalAlignment="Center" Margin="0,10,0,0"/>
                    <ComboBox x:Name="cmbLanguage" Grid.Row="1" Grid.Column="1" Margin="3,10,3,3" SelectedValuePath="Tag" SelectedValue="{Binding LanguageTag, Mode=TwoWay}"
                              ToolTip="Влияет только на окно выбора профиля каталога, которое показывается при первом запуске. Остальной интерфейс приложения переведён не полностью и остаётся на русском.">
                        <ComboBoxItem Content="Автоматически" Tag="auto"/>
                        <ComboBoxItem Content="Русский" Tag="ru"/>
                        <ComboBoxItem Content="English" Tag="en"/>
                    </ComboBox>
                    <TextBlock Grid.Row="2" Text="Плотность" VerticalAlignment="Center" Margin="0,10,0,0"/>
                    <CheckBox x:Name="chkCompactMode" Grid.Row="2" Grid.Column="1" Content="Компактные строки каталога"
                              Margin="3,10,3,3" IsChecked="{Binding CompactMode, Mode=TwoWay}"/>
                    <CheckBox x:Name="chkReduceMotion" Grid.Row="3" Grid.Column="1" Content="Уменьшить анимации"
                              Margin="3,10,3,3" IsChecked="{Binding ReduceMotion, Mode=TwoWay}"/>
                    <StackPanel Grid.Column="2" Grid.RowSpan="3" Orientation="Horizontal" VerticalAlignment="Center" Margin="20,0,0,0">
                        <Ellipse Width="18" Height="18" Fill="#4ADE80" Margin="3"/><Ellipse Width="18" Height="18" Fill="#38BDF8" Margin="3"/>
                        <Ellipse Width="18" Height="18" Fill="#FBBF24" Margin="3"/><Ellipse Width="18" Height="18" Fill="#F87171" Margin="3"/>
                    </StackPanel>
                </Grid>
            </GroupBox>
            
            <!-- Уведомления -->
            <GroupBox Header="🔔 Уведомления" Margin="0,0,0,15">
                <StackPanel>
                    <CheckBox x:Name="chkNotifications" Content="Показывать уведомления о завершении установки" 
                              Margin="10" Foreground="{DynamicResource TextPrimary}" IsChecked="{Binding NotifyInstallComplete, Mode=TwoWay}"/>
                    <CheckBox x:Name="chkUpdateNotifications" Content="Проверять обновления при запуске" 
                              Margin="10" Foreground="{DynamicResource TextPrimary}" IsChecked="{Binding NotifyAppUpdates, Mode=TwoWay}"/>
                </StackPanel>
            </GroupBox>
            
            <!-- Установка приложений -->
            <GroupBox Header="📦 Установка приложений" Margin="0,0,0,15">
                <StackPanel Margin="10">
                    <CheckBox x:Name="chkSilentInstall"
                              Content="Ставить приложения без окна установщика (только источник winget)"
                              Foreground="{DynamicResource TextPrimary}" Margin="0,0,0,4"
                              IsChecked="{Binding SilentInstall, Mode=TwoWay}"/>
                    <TextBlock Text="Добавляет winget флаг --silent. Действует только когда приложение ставится через winget, и только если сам пакет поддерживает тихий режим — иначе окно установщика всё равно появится. На установку через Chocolatey не влияет. Для прямых ссылок флаги тихого режима берутся из каталога, а этот флажок используется лишь как запасной вариант, когда в каталоге они не указаны."
                               TextWrapping="Wrap" Foreground="{DynamicResource TextSecondary}"
                               FontSize="11" Margin="0,0,0,14"/>

                    <TextBlock Text="Папка установки по умолчанию (только winget):"
                               Foreground="{DynamicResource TextPrimary}" Margin="0,0,0,5"/>
                    <Grid Margin="0,0,0,4">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="Auto"/>
                        </Grid.ColumnDefinitions>
                        <TextBox x:Name="txtDefaultInstallFolder" Height="30" Margin="0,0,8,0"
                                 Background="{DynamicResource CardBackground}"
                                 Foreground="{DynamicResource TextPrimary}"
                                 VerticalContentAlignment="Center" Padding="6,0"
                                 ToolTip="Абсолютный путь на локальном диске, например D:\Programs. Пустое поле — winget выбирает папку сам."
                                 Text="{Binding DefaultInstallFolderText, Mode=TwoWay, UpdateSourceTrigger=LostFocus}"/>
                        <Button x:Name="btnBrowseDefaultInstallFolder" Content="📁" Width="34" Height="30"
                                ToolTip="Откроет выбор папки, которую winget будет получать как --location."
                                Grid.Column="1" Command="{Binding BrowseDefaultInstallFolderCommand}"/>
                    </Grid>
                    <TextBlock Text="Передаётся winget как --location. Многие установщики этот параметр игнорируют и ставятся туда, куда заложено в самом пакете; приложения из Microsoft Store (msstore) его не поддерживают вовсе. Если в каталоге выбран несистемный диск установки, папка используется только когда она лежит на этом же диске — иначе подставляется «Program Files» выбранного диска. Путь должен быть абсолютным и локальным: сетевые пути и кавычки не принимаются. Пустое поле — winget выбирает папку сам."
                               TextWrapping="Wrap" Foreground="{DynamicResource TextSecondary}"
                               FontSize="11" Margin="0,0,0,4"/>
                    <TextBlock x:Name="txtDefaultInstallFolderStatus" Text="{Binding DefaultInstallFolderStatusText}"
                               TextWrapping="Wrap" FontSize="11"
                               Foreground="{DynamicResource TextSecondary}"/>
                </StackPanel>
            </GroupBox>

            <GroupBox Header="📚 Область каталога" Margin="0,0,0,15">
                <StackPanel Margin="10">
                    <TextBlock Text="Сколько приложений показывать в каталоге:"
                               Foreground="{DynamicResource TextPrimary}" Margin="0,0,0,5"/>
                    <ComboBox x:Name="cmbCatalogMode" Width="260" HorizontalAlignment="Left"
                              SelectedValuePath="Tag" SelectedValue="{Binding CatalogModeTag, Mode=TwoWay}">
                        <ComboBoxItem Content="Базовый" Tag="basic"/>
                        <ComboBoxItem Content="Расширенный" Tag="extended"/>
                        <ComboBoxItem Content="Полный" Tag="full"/>
                    </ComboBox>
                    <TextBlock Text="Выбор при первом запуске сохраняется навсегда, если не поменять его здесь. «Базовый» — только самые ходовые программы, «Полный» — весь каталог."
                               TextWrapping="Wrap" Foreground="{DynamicResource TextSecondary}"
                               FontSize="11" Margin="0,4,0,0"/>
                </StackPanel>
            </GroupBox>

            <!-- Таймауты -->
            <GroupBox Header="⏱️ Таймауты" Margin="0,0,0,15">
                <Grid Margin="10">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto"/>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="Auto"/>
                    </Grid.ColumnDefinitions>
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                    </Grid.RowDefinitions>
                    
                    <TextBlock Text="Таймаут загрузки каталога:" Grid.Row="0" Grid.Column="0" 
                               Foreground="{DynamicResource TextPrimary}" VerticalAlignment="Center"/>
                    <Slider x:Name="sliderCatalogTimeout" Grid.Row="0" Grid.Column="1" 
                            Minimum="3" Maximum="30" Value="{Binding CatalogTimeoutValue, Mode=TwoWay}" Margin="10,0" 
                            Foreground="{DynamicResource AccentColor}"/>
                    <TextBlock x:Name="txtCatalogTimeout" Grid.Row="0" Grid.Column="2" 
                               Text="{Binding CatalogTimeoutText}" Foreground="{DynamicResource TextSecondary}"/>
                    
                    <TextBlock Text="Таймаут проверки доступности:" Grid.Row="1" Grid.Column="0" 
                               Foreground="{DynamicResource TextPrimary}" VerticalAlignment="Center" Margin="0,10,0,0"/>
                    <Slider x:Name="sliderCheckTimeout" Grid.Row="1" Grid.Column="1" 
                            Minimum="5" Maximum="60" Value="{Binding CheckTimeoutValue, Mode=TwoWay}" Margin="10,10,10,0" 
                            Foreground="{DynamicResource AccentColor}"/>
                    <TextBlock x:Name="txtCheckTimeout" Grid.Row="1" Grid.Column="2" 
                               Text="{Binding CheckTimeoutText}" Foreground="{DynamicResource TextSecondary}" Margin="0,10,0,0"/>
                </Grid>
            </GroupBox>
            <!-- Обновления приложений -->
            <GroupBox Header="🔔 Обновления приложений" Margin="0,0,0,15">
                <StackPanel>
                    <Button x:Name="btnCheckUpdates" Content="🔍 Проверить обновления"
                            ToolTip="Проверит через winget доступные обновления приложений. Ничего не устанавливает."
                            Height="35" Margin="10" HorizontalAlignment="Left" Width="200"
                            Command="{Binding CheckUpdatesCommand}"/>
                    <TextBox x:Name="txtUpdatesLog" Text="{Binding UpdatesLogText, Mode=OneWay}" Margin="10,0,10,10" Height="100"
                             Background="{DynamicResource CardBackground}" Foreground="{DynamicResource TextPrimary}"
                             FontFamily="Consolas" FontSize="10" IsReadOnly="True"
                             VerticalScrollBarVisibility="Auto" TextWrapping="Wrap"/>
                </StackPanel>
            </GroupBox>

            
                    </StackPanel>
                </ScrollViewer>
            </TabItem>
            <TabItem Header="Источники">
                <!-- Карточка (Border, не GroupBox) растянута на весь TabItem через
                     Grid-родитель (а не StackPanel — тот игнорирует VerticalAlignment
                     ="Stretch" по направлению стека), чтобы рамка с фоном CardBackground
                     реально доходила до низа вкладки, а не заканчивалась сразу под
                     контентом с чёрной пустотой ниже (эта под-вкладка заметно короче
                     «Общие»/«Профиль и снимки», но делит с ними общий TabControl без
                     фиксированной высоты). Именно Border, а не общий стиль GroupBox
                     (App.xaml) — у GroupBox в этом проекте свой ControlTemplate, чей
                     корневой Grid на практике не растягивается вертикально даже с
                     VerticalAlignment="Stretch" на самом GroupBox (проверено диагностикой
                     с Background="Red" — под GroupBox оставалась пустая полоса вплоть до
                     низа вкладки); DockPanel + Border такой проблемы не имеет. -->
                <Grid Margin="8,12,8,8">
                    <Border Background="{DynamicResource CardBackground}"
                            BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1"
                            CornerRadius="8">
                        <DockPanel>
                            <Border DockPanel.Dock="Top" Background="{DynamicResource CardBackground}"
                                    BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,0,0,1"
                                    CornerRadius="8,8,0,0" Padding="8,4">
                                <TextBlock Text="🔀 Порядок источников установки"
                                           Foreground="{DynamicResource HeaderForeground}"
                                           FontWeight="Bold" FontSize="13"/>
                            </Border>
                            <ScrollViewer VerticalScrollBarVisibility="Auto">
                <StackPanel Margin="10">
                    <!-- Mode -->
                    <StackPanel Orientation="Horizontal" Margin="0,0,0,10">
                        <RadioButton x:Name="rbSourceGlobal"
                                     Content="Единый для всего каталога"
                                     GroupName="SourceMode"
                                     Foreground="{DynamicResource TextPrimary}"
                                     IsChecked="{Binding IsGlobalSourceMode, Mode=TwoWay}"
                                     Margin="0,0,20,0"/>
                        <RadioButton x:Name="rbSourcePerCategory"
                                     Content="Выбрать приоритетный источник по категориям"
                                     GroupName="SourceMode"
                                     Foreground="{DynamicResource TextPrimary}"
                                     IsChecked="{Binding IsPerCategorySourceMode, Mode=TwoWay}"/>
                    </StackPanel>

                    <!-- Global order editor -->
                    <Border x:Name="pnlGlobalOrder" Margin="0,0,0,10"
                            Visibility="{Binding ShowGlobalOrderPanel, Converter={StaticResource BoolToVis}}">
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="Auto"/>
                            </Grid.ColumnDefinitions>
                            <Border Background="{DynamicResource CardBackground}"
                                    CornerRadius="8" Padding="4">
                                <ListBox x:Name="lstSourceOrder"
                                         ItemsSource="{Binding SourceItems}"
                                         SelectedIndex="{Binding SelectedSourceIndex, Mode=TwoWay}"
                                         Background="Transparent"
                                         BorderThickness="0"
                                         Height="120"
                                         SelectionMode="Single">
                                    <ListBox.ItemTemplate>
                                        <DataTemplate>
                                            <TextBlock Text="{Binding Label}"
                                                       Foreground="{DynamicResource TextPrimary}"
                                                       Padding="6,4" FontSize="13"/>
                                        </DataTemplate>
                                    </ListBox.ItemTemplate>
                                </ListBox>
                            </Border>
                            <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="8,0,0,0">
                                <Button x:Name="btnSrcUp" Content="▲" Width="32" Height="32"
                                        ToolTip="Поднимет выбранный источник выше в порядке попыток установки."
                                        Margin="0,0,0,6" Command="{Binding MoveSourceUpCommand}"/>
                                <Button x:Name="btnSrcDown" Content="▼" Width="32" Height="32"
                                        ToolTip="Опустит выбранный источник ниже в порядке попыток установки."
                                        Command="{Binding MoveSourceDownCommand}"/>
                            </StackPanel>
                        </Grid>
                    </Border>

                    <!-- Per-category hint -->
                    <Border x:Name="pnlPerCategoryHint"
                            Visibility="{Binding ShowPerCategoryHint, Converter={StaticResource BoolToVis}}"
                            Background="{DynamicResource CardBackground}" CornerRadius="8" Padding="12">
                        <TextBlock TextWrapping="Wrap"
                                   Foreground="{DynamicResource TextSecondary}" FontSize="12">
                            <Run Text="После сохранения откройте вкладку "/>
                            <Run Text="Каталог" FontWeight="Bold"
                                 Foreground="{DynamicResource AccentColor}"/>
                            <Run Text=" — рядом с заголовком каждой категории появится выбор приоритетного источника."/>
                        </TextBlock>
                    </Border>

                    <!-- Save button -->
                    <Button x:Name="btnSaveSourceOrder"
                            Content="💾 Сохранить и перепроверить доступность"
                            ToolTip="Сохранит порядок источников и заново проверит доступность приложений по новым правилам."
                            Height="36" Margin="0,12,0,0"
                            FontWeight="SemiBold"
                            Command="{Binding SaveSourceOrderCommand}"/>

                    <TextBlock x:Name="txtSourceOrderStatus" Text="{Binding SourceOrderStatusText}" Margin="0,6,0,0"
                               FontSize="11" Foreground="{DynamicResource TextSecondary}"/>
                </StackPanel>
                            </ScrollViewer>
                        </DockPanel>
                    </Border>
                </Grid>
            </TabItem>
            <TabItem Header="Офлайн и приватность">
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                    <StackPanel Margin="8,12,8,8">
            <!-- Офлайн режим -->
            <GroupBox Header="🔌 Офлайн режим" Margin="0,0,0,15">
                <StackPanel Margin="10">
                    <!-- Toggle -->
                    <CheckBox x:Name="chkOfflineMode"
                              Content="Принудительный офлайн режим (скрыть вкладки, требующие интернет)"
                              Foreground="{DynamicResource TextPrimary}" Margin="0,0,0,4"
                              IsChecked="{Binding OfflineMode, Mode=TwoWay}"/>
                    <TextBlock Text="Вкладки Office, Активация и Сеть скрываются автоматически при потере соединения. Этот флаг форсирует офлайн вручную."
                               TextWrapping="Wrap" Foreground="{DynamicResource TextSecondary}"
                               FontSize="11" Margin="0,0,0,14"/>

                    <!-- Force online override -->
                    <CheckBox x:Name="chkForceOnlineMode"
                              Content="Принудительный онлайн-режим"
                              Foreground="{DynamicResource TextPrimary}" Margin="0,0,0,4"
                              IsChecked="{Binding ForceOnlineMode, Mode=TwoWay}"/>
                    <TextBlock Text="Всегда считать соединение активным (для VPN/прокси). Игнорирует автодетект сети, чтобы онлайн-вкладки не скрывались при ложноотрицательном определении."
                               TextWrapping="Wrap" Foreground="{DynamicResource TextSecondary}"
                               FontSize="11" Margin="0,0,0,14"/>

                    <!-- Параноидальный режим -->
                    <CheckBox x:Name="chkParanoidMode"
                              Content="Параноидальный режим — отключить фоновую и диагностическую сетевую активность"
                              Foreground="{DynamicResource TextPrimary}" Margin="0,0,0,4"
                              IsChecked="{Binding ParanoidMode, Mode=TwoWay}"/>
                    <TextBlock Text="Блокирует отправку краш-отчётов, отзывов, фоновые проверки обновлений, а также всю диагностику на вкладке «Сеть» (пинг, проверка сервисов, DNS, определение публичного IP) — в том числе ручную. Лаунчер тоже сверяется с этим флажком и перестаёт предлагать опубликовать отчёт о сбое или о неудачных установках публичным issue на GitHub; уже отложенный отчёт при этом удаляется, а не ждёт на диске. Дополнительно перестают загружаться значки приложений, а индикатор доступности в каталоге становится серым («неизвестно») — и то, и другое требует обращения к сторонним сайтам. Загрузка самого каталога и скачивание/установка приложений продолжают работать."
                               TextWrapping="Wrap" Foreground="{DynamicResource TextSecondary}"
                               FontSize="11" Margin="0,0,0,14"/>

                    <!-- Connectivity status -->
                    <Border x:Name="pnlConnStatus" CornerRadius="8" Padding="10,7" Margin="0,0,0,14"
                            Background="{Binding ConnStatusBackground}">
                        <StackPanel Orientation="Horizontal">
                            <TextBlock x:Name="txtConnIcon" Text="{Binding ConnIconText}" FontSize="14" Margin="0,0,8,0" VerticalAlignment="Center"/>
                            <TextBlock x:Name="txtConnStatus" Text="{Binding ConnStatusText}"
                                       Foreground="{DynamicResource TextPrimary}" FontSize="13"
                                       VerticalAlignment="Center"/>
                        </StackPanel>
                    </Border>

                    <!-- Cache path -->
                    <TextBlock Text="Папка для кэширования установщиков:"
                               Foreground="{DynamicResource TextPrimary}" Margin="0,0,0,5"/>
                    <Grid Margin="0,0,0,10">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="Auto"/>
                        </Grid.ColumnDefinitions>
                        <TextBox x:Name="txtOfflineCachePath" Height="30" Margin="0,0,8,0"
                                 Background="{DynamicResource CardBackground}"
                                 Foreground="{DynamicResource TextPrimary}"
                                 VerticalContentAlignment="Center" Padding="6,0"
                                 Text="{Binding OfflineCachePathText, Mode=TwoWay, UpdateSourceTrigger=LostFocus}"/>
                        <Button x:Name="btnBrowseCachePath" Content="📁" Width="34" Height="30"
                                ToolTip="Откроет выбор папки для хранения офлайн-кэша установщиков."
                                Grid.Column="1" Command="{Binding BrowseCachePathCommand}"/>
                    </Grid>

                    <!-- Cache stats -->
                    <Border Background="{DynamicResource CardBackground}" CornerRadius="8"
                            Padding="12,8" Margin="0,0,0,12">
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="Auto"/>
                                <ColumnDefinition Width="Auto"/>
                            </Grid.ColumnDefinitions>
                            <TextBlock x:Name="txtCacheStats" Text="{Binding CacheStatsText}"
                                       Foreground="{DynamicResource TextSecondary}" FontSize="12"
                                       VerticalAlignment="Center"/>
                            <Button x:Name="btnOpenCacheFolder" Content="📂 Открыть" Width="90" Height="28"
                                    ToolTip="Откроет текущую папку офлайн-кэша в Проводнике."
                                    Grid.Column="1" Margin="0,0,8,0" Command="{Binding OpenCacheFolderCommand}"/>
                            <Button x:Name="btnClearCache" Content="🗑️ Очистить" Width="90" Height="28"
                                    ToolTip="После подтверждения удалит сохранённые установщики из офлайн-кэша."
                                    Grid.Column="2" Command="{Binding ClearCacheCommand}"/>
                        </Grid>
                    </Border>

                    <!-- App selection for caching -->
                    <TextBlock Text="Выберите приложения для кэширования:"
                               Foreground="{DynamicResource TextPrimary}" Margin="0,0,0,6"/>
                    <Border Background="{DynamicResource CardBackground}" CornerRadius="8"
                            Padding="8" Margin="0,0,0,10" MaxHeight="220">
                        <ScrollViewer VerticalScrollBarVisibility="Auto">
                            <StackPanel>
                                <Grid Margin="0,0,0,6">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="*"/>
                                        <ColumnDefinition Width="Auto"/>
                                        <ColumnDefinition Width="Auto"/>
                                    </Grid.ColumnDefinitions>
                                    <TextBox x:Name="txtCacheAppFilter" Height="26" Margin="0,0,8,0"
                                             Background="{DynamicResource ContentBackground}"
                                             Foreground="{DynamicResource TextPrimary}"
                                             VerticalContentAlignment="Center" Padding="6,0"
                                             Tag="Поиск по названию..."
                                             Text="{Binding CacheAppFilterText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"/>
                                    <Button x:Name="btnCacheSelectAll" Content="Все" Width="44" Height="26"
                                            ToolTip="Отметит все приложения, показанные в списке подготовки офлайн-кэша."
                                            Grid.Column="1" Margin="0,0,6,0" Command="{Binding SelectAllCacheCommand}"/>
                                    <Button x:Name="btnCacheSelectNone" Content="Сброс" Width="52" Height="26"
                                            ToolTip="Снимет отметки со всех приложений для загрузки в кэш."
                                            Grid.Column="2" Command="{Binding SelectNoneCacheCommand}"/>
                                </Grid>
                                <ItemsControl x:Name="listCacheApps" ItemsSource="{Binding FilteredCacheApps}">
                                    <ItemsControl.ItemTemplate>
                                        <DataTemplate>
                                            <CheckBox Content="{Binding DisplayName}" IsChecked="{Binding IsSelected, Mode=TwoWay}"
                                                      Foreground="{DynamicResource TextPrimary}"
                                                      Margin="2,2"/>
                                        </DataTemplate>
                                    </ItemsControl.ItemTemplate>
                                </ItemsControl>
                            </StackPanel>
                        </ScrollViewer>
                    </Border>

                    <!-- Download button + progress -->
                    <Grid Margin="0,0,0,8">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="Auto"/>
                        </Grid.ColumnDefinitions>
                        <Button x:Name="btnDownloadToCache" Height="36"
                                Content="⬇️ Скачать выбранные в кэш"
                                ToolTip="Скачает установщики отмеченных приложений и проверит их перед сохранением для офлайн-установки."
                                FontWeight="SemiBold" Command="{Binding DownloadToCacheCommand}"
                                Margin="0,0,8,0"/>
                        <Button x:Name="btnCancelCacheDownload" Height="36" Width="80"
                                Content="⏹ Стоп" Grid.Column="1"
                                ToolTip="Остановит загрузку оставшихся установщиков. Уже проверенные файлы сохранятся в кэше."
                                Visibility="{Binding ShowCancelCacheDownload, Converter={StaticResource BoolToVis}}"
                                IsEnabled="{Binding CanCancelCacheDownload}"
                                Command="{Binding CancelCacheDownloadCommand}"/>
                    </Grid>

                    <ProgressBar x:Name="progressCache" Height="6" Minimum="0" Maximum="100"
                                 Value="{Binding CacheProgressValue, Mode=OneWay}" Margin="0,0,0,6"
                                 Foreground="{DynamicResource AccentColor}"
                                 Background="{DynamicResource BorderBrush}"
                                 Visibility="{Binding ShowCacheProgress, Converter={StaticResource BoolToVis}}"/>
                    <TextBox x:Name="txtCacheLog" Text="{Binding CacheLogText, Mode=OneWay}" Height="80" Margin="0,0,0,0"
                             Background="{DynamicResource ContentBackground}"
                             Foreground="#00FF00" FontFamily="Consolas" FontSize="10"
                             IsReadOnly="True" TextWrapping="Wrap"
                             VerticalScrollBarVisibility="Auto"
                             Visibility="{Binding ShowCacheLog, Converter={StaticResource BoolToVis}}"/>
                </StackPanel>
            </GroupBox>

                    </StackPanel>
                </ScrollViewer>
            </TabItem>
            <TabItem Header="Профиль и снимки">
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                    <StackPanel Margin="8,12,8,8">
            <!-- Поведение приложения -->
            <GroupBox Header="🖥️ Поведение приложения" Margin="0,0,0,15">
                <StackPanel Margin="10">
                    <CheckBox x:Name="chkMinimizeToTray"
                              Content="Сворачивать в трей при закрытии"
                              Foreground="{DynamicResource TextPrimary}"
                              Margin="0,0,0,4"
                              IsChecked="{Binding MinimizeToTray, Mode=TwoWay}"/>
                    <TextBlock Text="Окно скрывается в трей вместо закрытия. Двойной клик по иконке для открытия."
                               TextWrapping="Wrap" Foreground="{DynamicResource TextSecondary}"
                               FontSize="11"/>
                </StackPanel>
            </GroupBox>

            <!-- Перенос настроек -->
            <GroupBox Header="💾 Перенос настроек" Margin="0,0,0,15">
                <StackPanel Margin="10">
                    <TextBlock Text="Профиль, пресеты, избранное и настройки сохраняются в один файл для переноса на другой компьютер. Данные не покидают устройство — файл переносится вручную."
                               TextWrapping="Wrap" Foreground="{DynamicResource TextSecondary}"
                               FontSize="11" Margin="0,0,0,10"/>
                    <StackPanel Orientation="Horizontal">
                        <Button x:Name="btnExportSettings" Content="📤 Экспорт настроек"
                                ToolTip="Сохранит настройки Ven4Tools в выбранный JSON-файл."
                                Height="35" Width="180" Margin="0,0,10,0"
                                Command="{Binding ExportSettingsCommand}"/>
                        <Button x:Name="btnImportSettings" Content="📥 Импорт настроек"
                                ToolTip="Загрузит настройки из JSON-файла и применит поддерживаемые параметры."
                                Height="35" Width="180"
                                Command="{Binding ImportSettingsCommand}"/>
                    </StackPanel>
                    <TextBlock x:Name="txtTransferStatus" Text="{Binding TransferStatusText}" Margin="0,8,0,0"
                               FontSize="11" Foreground="{DynamicResource TextSecondary}"/>
                </StackPanel>
            </GroupBox>

            <!-- Скрытые приложения -->
            <GroupBox Header="🙈 Скрытые приложения" Margin="0,0,0,15">
                <StackPanel Margin="10">
                    <TextBlock Text="Приложения, скрытые из каталога кнопкой 🙈 в строке. Индивидуального списка нет — только массовый возврат всех сразу."
                               TextWrapping="Wrap" Foreground="{DynamicResource TextSecondary}"
                               FontSize="11" Margin="0,0,0,10"/>
                    <Button x:Name="btnUnhideAllApps" Content="👁 Показать скрытые"
                            ToolTip="Вернёт в каталог все скрытые приложения."
                            Height="35" Width="180" HorizontalAlignment="Left"
                            Command="{Binding UnhideAllAppsCommand}"/>
                    <TextBlock x:Name="txtHiddenAppsStatus" Text="{Binding HiddenAppsStatusText}" Margin="0,8,0,0"
                               FontSize="11" Foreground="{DynamicResource TextSecondary}"/>
                </StackPanel>
            </GroupBox>

            <!-- Снапшоты конфигурации -->
            <GroupBox Header="📸 Снапшоты конфигурации" Margin="0,0,0,15">
                <StackPanel Margin="10">
                    <TextBlock TextWrapping="Wrap" Foreground="{DynamicResource TextSecondary}"
                               FontSize="11" Margin="0,0,0,10">
                        Сохранение и повторное применение набора настроек на этой машине:
                        запоминает отмеченные твики Debloater и локальные пресеты. Это не
                        отмена изменений — твики, применённые после создания снапшота,
                        назад не возвращаются. Не зависит от точки восстановления Windows.
                    </TextBlock>

                    <Button x:Name="btnSaveSnapshot" Content="📸 Сохранить снапшот"
                            ToolTip="Сохранит текущие твики очистки и пресеты, чтобы их можно было восстановить позже."
                            Height="34" HorizontalAlignment="Left" Width="200"
                            Margin="0,0,0,10" Command="{Binding SaveSnapshotCommand}"/>

                    <Border Background="{DynamicResource CardBackground}" CornerRadius="8"
                            Padding="6" MaxHeight="260">
                        <ScrollViewer VerticalScrollBarVisibility="Auto">
                            <ItemsControl x:Name="lstSnapshots" ItemsSource="{Binding Snapshots}">
                                <ItemsControl.ItemTemplate>
                                    <DataTemplate>
                                        <Border Margin="0,2" Padding="8,6"
                                                Background="{DynamicResource ContentBackground}"
                                                CornerRadius="6">
                                            <Grid>
                                                <Grid.ColumnDefinitions>
                                                    <ColumnDefinition Width="*"/>
                                                    <ColumnDefinition Width="Auto"/>
                                                </Grid.ColumnDefinitions>
                                                <TextBlock Text="{Binding DisplayLabel}"
                                                           FontSize="12"
                                                           Foreground="{DynamicResource TextPrimary}"
                                                           TextWrapping="Wrap"
                                                           VerticalAlignment="Center"/>
                                                <StackPanel Grid.Column="1" Orientation="Horizontal">
                                                    <Button Content="↺ Восстановить"
                                                            Height="26" Padding="8,0"
                                                            FontSize="11" Margin="6,0,0,0"
                                                            ToolTip="После подтверждения восстановит твики очистки и пресеты из этого снапшота."
                                                            Command="{Binding DataContext.RestoreSnapshotCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                                            CommandParameter="{Binding}"/>
                                                    <Button Content="✕"
                                                            Width="26" Height="26" Padding="0"
                                                            FontSize="11" Margin="4,0,0,0"
                                                            Background="Transparent"
                                                            Foreground="{DynamicResource TextSecondary}"
                                                            BorderThickness="0"
                                                            ToolTip="После подтверждения навсегда удалит этот сохранённый снапшот."
                                                            Command="{Binding DataContext.DeleteSnapshotCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                                            CommandParameter="{Binding}"/>
                                                </StackPanel>
                                            </Grid>
                                        </Border>
                                    </DataTemplate>
                                </ItemsControl.ItemTemplate>
                            </ItemsControl>
                        </ScrollViewer>
                    </Border>

                    <TextBlock x:Name="txtSnapshotsEmpty"
                               Text="Нет сохранённых снапшотов"
                               FontSize="11" Margin="6,6,6,0"
                               Foreground="{DynamicResource TextSecondary}"
                               Visibility="{Binding ShowSnapshotsEmpty, Converter={StaticResource BoolToVis}}"/>

                    <TextBlock x:Name="txtSnapshotStatus" Text="{Binding SnapshotStatusText}" Margin="0,8,0,0"
                               FontSize="11" TextWrapping="Wrap"
                               Foreground="{DynamicResource TextSecondary}"/>
                </StackPanel>
            </GroupBox>
                    </StackPanel>
                </ScrollViewer>
            </TabItem>
        </TabControl>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Переписать `Ven4Tools/Views/Tabs/SystemTab.xaml.cs`**

Полное содержимое файла:

```csharp
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Services;
using Ven4Tools.Shared;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «Настройки» — тонкая обёртка над <see cref="SystemViewModel"/>.
    /// Вся логика перенесена в ViewModel при MVVM-миграции (2026-08-26, девятая
    /// вкладка после Debloater/History/About/Activation/Network/Office/Installed/
    /// Diagnostics). Три делегата (OwnerWindowProvider/DebloaterTabProvider/
    /// RefreshTabVisibility) и подписки на события ThemeApplied/
    /// ConnectivityStatusUpdated/CacheLogAppended — единственное, что остаётся
    /// здесь, потому что требует живой Window/UIElement, которого у VM нет.
    /// </summary>
    public partial class SystemTab : UserControl
    {
        private readonly SystemViewModel _viewModel = new();
        private bool _initialized = false;
        private bool _connSubscribed = false;

        public SystemTab()
        {
            InitializeComponent();
            DataContext = _viewModel;

            _viewModel.OwnerWindowProvider = () => Window.GetWindow(this);
            _viewModel.DebloaterTabProvider = () => Window.GetWindow(this) is MainWindow mw ? mw.EnsureDebloaterTab() : null;
            _viewModel.RefreshTabVisibility = () => { if (Window.GetWindow(this) is MainWindow mw) mw.UpdateTabVisibility(); };

            _viewModel.ThemeApplied += () => MotionService.CrossFade((UIElement?)Window.GetWindow(this) ?? this, 220);
            _viewModel.ConnectivityStatusUpdated += () => MotionService.Pulse(pnlConnStatus, 1.015, 160);
            _viewModel.CacheLogAppended += () => txtCacheLog.ScrollToEnd();

            Loaded += SystemTab_Loaded;
            Unloaded += SystemTab_Unloaded;
        }

        private void OnConnectivityChanged(bool online) => Dispatcher.Invoke(_viewModel.UpdateConnectivityStatus);

        private void SystemTab_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_connSubscribed)
            {
                ConnectivityMonitor.StatusChanged -= OnConnectivityChanged;
                _connSubscribed = false;
            }
        }

        private void SystemTab_Loaded(object sender, RoutedEventArgs e)
        {
            // Переподписка при каждом показе вкладки (после Unloaded подписка снимается)
            if (!_connSubscribed)
            {
                ConnectivityMonitor.StatusChanged += OnConnectivityChanged;
                _connSubscribed = true;
            }
            _viewModel.UpdateConnectivityStatus();

            if (_initialized) return;
            _initialized = true;

            _viewModel.Initialize();
        }
    }
}
```

- [ ] **Step 3: Удалить перенесённые partial-файлы code-behind**

```bash
git rm Ven4Tools/Views/Tabs/SystemTab.Appearance.cs Ven4Tools/Views/Tabs/SystemTab.Settings.cs Ven4Tools/Views/Tabs/SystemTab.AppUpdates.cs Ven4Tools/Views/Tabs/SystemTab.Offline.cs Ven4Tools/Views/Tabs/SystemTab.Cache.cs Ven4Tools/Views/Tabs/SystemTab.Sources.cs Ven4Tools/Views/Tabs/SystemTab.Snapshots.cs
```

- [ ] **Step 4: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок, 0 предупреждений — во всех проектах, включая `Ven4Tools.ClientUITests`.

- [ ] **Step 5: Прогнать весь юнит-набор**

Run: `dotnet test tests/Ven4Tools.Tests -c Release`
Expected: без регрессий (было 471 после DiagnosticsTab + новые из `SystemViewModelTests` — итоговое число проверить фактическим прогоном, не предполагать заранее).

- [ ] **Step 6: Грep-проверка TwoWay-рисков перед коммитом**

```bash
grep -n "txtUpdatesLog\|txtCacheLog\|progressCache" Ven4Tools/Views/Tabs/SystemTab.xaml
```

Убедиться, что у `txtUpdatesLog`/`txtCacheLog`/`progressCache` есть `Mode=OneWay` на биндинге `Text=`/`Value=` (уже включено в код Step 1 — эта проверка на случай, если код при транскрипции разошёлся с планом).

- [ ] **Step 7: Commit**

```bash
git add Ven4Tools/Views/Tabs/SystemTab.xaml Ven4Tools/Views/Tabs/SystemTab.xaml.cs
git commit -m "refactor(system): SystemTab — тонкая обёртка над SystemViewModel"
```

---

### Task 3: Верификация — регрессия существующих тестов

**Files:**
- Не создаёт и не меняет файлы.

**Interfaces:**
- Не применимо.

- [ ] **Step 1: Полная сборка Release**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0/0.

- [ ] **Step 2: Юнит-тесты целиком на VenchWork**

Run (на VenchWork): `dotnet test tests/Ven4Tools.Tests -c Release`
Expected: без регрессий относительно числа тестов после Task 2.

- [ ] **Step 3: Существующие UI-тесты на VenchWork**

Run (на VenchWork): `dotnet test Ven4Tools.ClientUITests -c Release --filter "FullyQualifiedName~Phase2SystemTabTests|FullyQualifiedName~SourceOrderSettingsUiTests|FullyQualifiedName~KeyButtonsSmokeTests"`
Expected: `Phase2SystemTabTests` (включая реальное сохранение/удаление снапшота через диалог), `SourceOrderSettingsUiTests` (перестановка источника кнопкой ▼ + сохранение) — зелёные. `AuditFixesUiTests`, если время позволяет — релевантная часть про источники, не обязательна к прогону отдельно (пересекается с `SourceOrderSettingsUiTests`).

**Если UI-прогон не укладывается в 10-15 минут** — не ждать дальше: ребутнуть VenchWork / подключить Opus 5 для диагностики / искать причину самостоятельно, начиная с `%LOCALAPPDATA%\Ven4Tools\crash_last.json` (см. `feedback_ui_test_hang_escalation` в памяти).

- [ ] **Step 4: Финальный коммит верификации**

```bash
git add -A
git status
git commit -m "test(system): MVVM-миграция SystemTab проверена на VenchWork" --allow-empty
```

- [ ] **Step 5: Финальное цельное ревью ветки**

Обязательный шаг перед мерджем. Пакет для ревью: `scripts/review-package <merge-base main mvvm-systemtab> HEAD`. **Явно поручить ревьюеру**:
1. Полный независимый грep всех биндингов нового `SystemTab.xaml` на TwoWay-по-умолчанию цели без `Mode=OneWay` на `private set`-свойствах — особое внимание `txtUpdatesLog`/`txtCacheLog`/`progressCache` (три места, где этот класс бага уже трижды случался в серии: OfficeTab/DiagnosticsTab дважды).
2. Урок InstalledTab на `IsGlobalSourceMode`/`IsPerCategorySourceMode` — оба сеттера безусловно вызывают `UpdateSourcePanels()`.
3. Три делегата (`OwnerWindowProvider`/`DebloaterTabProvider`/`RefreshTabVisibility`) реально заданы в `SystemTab.xaml.cs` и используются в VM там же, где в оригинале.
4. Реентерабельность `RunCheckUpdatesAsync`/`RunDownloadToCacheAsync`/`RunSaveSnapshotAsync`/`RunRestoreSnapshotAsync` (гейт первой строкой/по параметру).
5. `ThemeService`/тема НЕ тронуты сверх прямого переноса вызовов `ThemeService.Apply(...)`.

- [ ] **Step 6: Merge + push в `main`** (без дополнительного вопроса — автономная сессия)

```bash
git checkout main
git merge --ff-only mvvm-systemtab
dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental
git push origin main
git branch -d mvvm-systemtab
```

Перед пушем — обязательно проверить все коммиты ветки на `Claude-Session`-трейлер: `git log main..mvvm-systemtab --format="%B" | grep -i claude` (должно быть пусто).

---

## После задачи

Смержено и запушено в `main`. SystemTab была последней из явно перечисленных «оставшихся» вкладок в стандинг-директиве MVVM-миграции (девять вкладок мигрировано: Debloater/History/About/Activation/Network/Office/Installed/Diagnostics/System). Прежде чем продолжать на следующую вкладку — сверить с `agent_context.md`/`feature_map.md`, какие вкладки клиента ещё остались на code-behind, и обновить план серии.
