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
        private readonly SemaphoreSlim installSemaphore = new SemaphoreSlim(1);
        private bool isCancelled = false;
        private CancellationTokenSource? cancellationTokenSource;
        private readonly AppManager appManager = null!;
        private readonly InstallationService installService = null!;
        private readonly AvailabilityChecker availabilityChecker = null!;
        private readonly InstalledAppsService installedAppsService = new();
        private readonly UserAppsService _userAppsService = new();
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
        private bool _eventsSubscribed = false;
        private bool _showFavoritesOnly = false;
        private bool _showRuBlocked = true;
        private readonly System.Collections.Generic.HashSet<string> _ruBlockedIds = new();
        private CancellationTokenSource? _searchDebounce;
        private Action? _profileChangedHandler;
        public event Action? SwitchToUpdatesRequested;

        // Categories visible per mode
        private static readonly System.Collections.Generic.HashSet<AppCategory> BasicCategories = new()
        {
            AppCategory.Браузеры, AppCategory.Офис, AppCategory.Мультимедиа,
            AppCategory.Системные, AppCategory.Другое
        };
        private static readonly System.Collections.Generic.HashSet<AppCategory> ExtendedCategories = new(BasicCategories)
        {
            AppCategory.Разработка, AppCategory.Графика, AppCategory.Мессенджеры
        };

        public CatalogTab()
        {
            InitializeComponent();

            _catalogLoader   = new CatalogLoaderService();
            appManager       = new AppManager();
            installService   = new InstallationService();
            availabilityChecker = new AvailabilityChecker();

            InitCategoryPanels();
            LoadAvailableDisks();

            _profileChangedHandler = () => Dispatcher.Invoke(ApplyProfileFilters);

            Loaded += async (s, e) =>
            {
                // Loaded может срабатывать многократно (переключение вкладок) —
                // подписываемся только один раз, иначе обработчики дублируются.
                if (!_eventsSubscribed)
                {
                    ProfileService.Changed     += _profileChangedHandler;
                    UserSession.Changed        += OnUserSessionChanged;
                    AppSettings.Changed        += OnAppSettingsChanged;
                    SourceOrderService.Changed += OnSourceOrderChanged;
                    _eventsSubscribed = true;
                }
                if (!_initialized)
                {
                    _initialized = true;
                    await LoadCatalogAndRefreshAsync();
                }
                ApplyCategorySourceHeaders();
            };
            Unloaded += (_, _) =>
            {
                if (_eventsSubscribed)
                {
                    ProfileService.Changed     -= _profileChangedHandler;
                    UserSession.Changed        -= OnUserSessionChanged;
                    AppSettings.Changed        -= OnAppSettingsChanged;
                    SourceOrderService.Changed -= OnSourceOrderChanged;
                    _eventsSubscribed = false;
                }
                (installService as IDisposable)?.Dispose();
                (availabilityChecker as IDisposable)?.Dispose();
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

            _showRuBlocked = ProfileService.Current.ShowRuBlocked;
            btnShowRuBlocked.Click += BtnShowRuBlocked_Click;
            UpdateRuBlockedButton();
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
