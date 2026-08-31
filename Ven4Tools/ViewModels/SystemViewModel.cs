using System;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;
using Ven4Tools.Shared;
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
    public sealed partial class SystemViewModel : ViewModelBase
    {
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
        /// Первичное заполнение вкладки. Вызывается из code-behind при первом Loaded
        /// (гейт _initialized остался в SystemTab.xaml.cs — WPF-lifecycle забота, не
        /// VM-концерн). Все три источника данных читаются с диска: размер кэша, папка
        /// снапшотов и проверка «этот установщик уже скачан» по каждому приложению
        /// каталога (последнее — самое дорогое, порядка сотни File.Exists). На медленном
        /// носителе с большим кэшем они подвешивали UI-поток на всё первое открытие
        /// вкладки, поэтому каждое чтение вынесено в пул потоков. В потоке UI остаётся
        /// только применение готовых результатов: коллекции SourceItems/Snapshots и
        /// список кэша привязаны к разметке и меняются исключительно отсюда.
        /// </summary>
        public async Task InitializeAsync()
        {
            LoadSourceOrderUI();

            var cacheStats = await Task.Run(OfflineService.GetCacheStats);
            var snapshots  = await Task.Run(ConfigSnapshotService.GetSnapshots);
            var cachedIds  = await Task.Run(ScanCachedAppIds);

            ApplyCacheStats(cacheStats);
            LoadCacheAppsList(cachedIds);
            ApplySnapshots(snapshots);
        }
    }
}
