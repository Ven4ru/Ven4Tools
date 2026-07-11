using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class CatalogTab : UserControl
    {
        private static readonly HttpClient _httpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools");
            return client;
        }

        private readonly string[] wingetSources = { "winget", "msstore" };
        // Последовательная установка: два параллельных MSI вызывают конфликт
        // Windows Installer (ошибка 1618). Жертвуем скоростью ради надёжности.
        // Семафор общий на всё приложение — см. InstallationService.InstallSemaphore.
        private bool isCancelled = false;
        private CancellationTokenSource? cancellationTokenSource;
        // Отдельный источник отмены для ретраев проверки доступности: не зависит от
        // токена установки (его отмена не должна гасить независимые проверки).
        // Отменяется при закрытии окна приложения.
        private readonly CancellationTokenSource _availabilityCts = new();
        private bool _availabilityShutdownHooked = false;
        private readonly AppManager appManager = null!;
        private readonly InstallationService installService = null!;
        private readonly AvailabilityChecker availabilityChecker = null!;
        private readonly InstalledAppsService installedAppsService = new();
        private readonly FavoritesService _favoritesService = new();
        private Dictionary<string, CheckBox> appCheckBoxes = new();
        private Dictionary<AppCategory, StackPanel> categoryPanels = new();
        private Dictionary<string, AvailabilityChecker.AvailabilityStatus> availabilityStatus = new();
        private Dictionary<string, ComboBox> _versionCombos = new();
        private Dictionary<string, List<string>> _appVersions = new();
        private readonly VersionTrackingService _versionTracker = new();
        private MasterCatalog? _catalog;
        private CatalogLoaderService? _catalogLoader;
        private string selectedInstallDrive = "C:\\";
        private string systemDrive = "C:\\";
        private bool _isCheckingAvailability = false;
        private bool _isInstalling = false;
        private bool _initialized = false;

        /// <summary>Идёт ли сейчас установка приложений (для предупреждения при закрытии окна).</summary>
        public bool IsInstalling => _isInstalling;

        /// <summary>Выбранный пользователем диск установки (используется при установке из пина).</summary>
        public string SelectedInstallDrive => selectedInstallDrive;
        private bool _eventsSubscribed = false;
        // Версия порядка источников, отражённая в последней проверке доступности.
        // Нужна, чтобы поймать изменение порядка, произошедшее пока вкладка была
        // выгружена (не подписана на SourceOrderService.Changed) — см. Loaded.
        private int _lastSourceOrderVersion = SourceOrderService.Version;
        private bool _showFavoritesOnly = false;
        private CancellationTokenSource? _searchDebounce;
        private Action? _profileChangedHandler;
        public event Action? SwitchToUpdatesRequested;

        public CatalogTab()
        {
            InitializeComponent();

            _catalogLoader   = new CatalogLoaderService();
            appManager       = new AppManager();
            installService   = new InstallationService();
            availabilityChecker = new AvailabilityChecker();

            InitCategoryPanels();
            LoadAvailableDisks();
            InitPresets();

            _profileChangedHandler = () => Dispatcher.Invoke(ApplyProfileFilters);

            Loaded += async (s, e) =>
            {
                // Loaded может срабатывать многократно (переключение вкладок) —
                // подписываемся только один раз, иначе обработчики дублируются.
                if (!_eventsSubscribed)
                {
                    ProfileService.Changed     += _profileChangedHandler;
                    AppSettings.Changed        += OnAppSettingsChanged;
                    SourceOrderService.Changed += OnSourceOrderChanged;
                    _eventsSubscribed = true;
                }
                // Отменяем ретраи доступности при закрытии окна (вкладка кэшируется и
                // переиспользуется, поэтому привязываемся к закрытию окна, а не к Unloaded).
                if (!_availabilityShutdownHooked)
                {
                    var window = Window.GetWindow(this);
                    if (window != null)
                    {
                        window.Closed += (_, _) =>
                        {
                            // Только Cancel, без Dispose: отложенные ретраи проверки
                            // доступности ещё могут читать _availabilityCts.Token, и
                            // Dispose приводил к ObjectDisposedException — ложным
                            // «❌ Ошибка при проверке» в логе при закрытии окна.
                            // CTS живёт до конца процесса, утечки нет.
                            try { _availabilityCts.Cancel(); } catch { }
                        };
                        _availabilityShutdownHooked = true;
                    }
                }
                if (!_initialized)
                {
                    _initialized = true;
                    await LoadCatalogAndRefreshAsync();
                    // Первичная загрузка уже отражает текущий порядок источников.
                    _lastSourceOrderVersion = SourceOrderService.Version;
                }
                else if (_lastSourceOrderVersion != SourceOrderService.Version)
                {
                    // Порядок источников меняли (например, во вкладке «Система»), пока
                    // «Каталог» был выгружен и не получал событие Changed — запускаем
                    // обещанную перепроверку доступности сейчас, при открытии вкладки.
                    OnSourceOrderChanged();
                }
                ApplyCategorySourceHeaders();
            };
            Unloaded += (_, _) =>
            {
                if (_eventsSubscribed)
                {
                    ProfileService.Changed     -= _profileChangedHandler;
                    AppSettings.Changed        -= OnAppSettingsChanged;
                    SourceOrderService.Changed -= OnSourceOrderChanged;
                    _eventsSubscribed = false;
                }
                // Сервисы НЕ освобождаем: вкладка кэшируется в MainWindow и переиспользуется,
                // а поля readonly — после Dispose все запросы падали бы с ObjectDisposedException.
            };

            btnInstall.Click              += InstallSelected_Click;
            btnCancelInstall.Click        += CancelInstall_Click;
            btnCheckUpdates.Click         += BtnCheckUpdates_Click;
            btnClearAllUserApps.Click     += BtnClearAllUserApps_Click;
            btnRefreshCatalog.Click       += BtnRefreshCatalog_Click;
            btnRefreshAvailability.Click  += RefreshAvailability_Click;
            cmbAvailableDisks.SelectionChanged += CmbAvailableDisks_SelectionChanged;
            txtSearch.TextChanged         += TxtSearch_TextChanged;
            txtSearch.GotFocus            += (_, _) => { if (txtSearch.Text == (string)txtSearch.Tag) txtSearch.Text = ""; };
            txtSearch.LostFocus           += (_, _) => { if (string.IsNullOrWhiteSpace(txtSearch.Text)) txtSearch.Text = (string)txtSearch.Tag; };
            txtSearch.Text = (string)txtSearch.Tag;
            btnClearSearch.Click          += (_, _) => { txtSearch.Text = (string)txtSearch.Tag; };
            btnFavoritesOnly.Click        += BtnFavoritesOnly_Click;
            btnFavoritesOnly.Foreground    = new SolidColorBrush(Color.FromRgb(100, 100, 100));
        }

        private void InitCategoryPanels()
        {
            categoryPanels[AppCategory.Браузеры]         = PanelБраузеры;
            categoryPanels[AppCategory.Офис]             = PanelОфис;
            categoryPanels[AppCategory.Графика]          = PanelГрафика;
            categoryPanels[AppCategory.Разработка]       = PanelРазработка;
            categoryPanels[AppCategory.Мессенджеры]      = PanelМессенджеры;
            categoryPanels[AppCategory.Мультимедиа]      = PanelМультимедиа;
            categoryPanels[AppCategory.Системные]        = PanelСистемные;
            categoryPanels[AppCategory.ИгровыеСервисы]   = PanelИгровыеСервисы;
            categoryPanels[AppCategory.Драйверпаки]      = PanelДрайверпаки;
            categoryPanels[AppCategory.Другое]           = PanelДругое;
            categoryPanels[AppCategory.Пользовательские] = PanelПользовательские;
        }
    }
}
