using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;
using Ven4Tools.Shared;

namespace Ven4Tools.Launcher
{
    public partial class MainWindow : Window
    {
        // Один переиспользуемый клиент: создание нового HttpClient на каждый запрос
        // исчерпывает сокеты. Таймаут — бесконечный, отдельные операции ограничиваем
        // через CancellationToken на месте вызова.
        private static readonly HttpClient _httpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools-Launcher");
            return client;
        }

        private NotifyIcon?          _notifyIcon;
        private bool                 _minimizeToTray = true;
        private string               _settingsPath;
        private string               _installPath = "";
        private string               _clientPath  = "";
        private List<ClientVersionInfo> _availableVersions = new();
        private ClientVersionInfo?   _selectedVersion;
        private bool                 _clientUpdateAvailable = false;
        private bool                 _detailsPanelOpen = false;
        private System.Diagnostics.Process? _clientProcess;
        private CancellationTokenSource? _downloadCts;
        private UpdateBackgroundService? _updateService;
        private bool                 _backgroundUpdates = true;
        private bool                 _autostart         = false;
        private bool                 _startMinimized    = false;
        private bool                 _autoUpdateClient  = false;
        // Выбранный пользователем источник загрузки (переставляется в начало цепочки).
        private DownloadSource       _downloadSource    = DownloadSource.Auto;
        // Кэш последнего известного IP CDN из подписанного version.json (для IP-pinning).
        private string               _lastKnownCdnIp    = "";
        private SettingsWindow?      _settingsWindow;
        private readonly string      _dataFolderPath;
        // Запросы на установку компонентов из setup отложены до первого видимого показа
        // окна: при автозапуске в трее (скрытое окно) UAC/прогресс не должны всплывать незаметно
        private bool                 _pendingSetupComponents = false;
        // Окна отчётов (крэш клиента / неуспешные установки) при старте показываются
        // модально; при автозапуске в трее (скрытое окно) — откладываются до первого
        // показа окна, чтобы модальный диалог не всплывал поверх скрытого владельца.
        private bool                 _pendingStartupReports = false;
        private readonly bool        _isUiTestMode;
        private string               _lastNotifiedLauncherVersion = "";
        private string               _lastNotifiedClientVersion   = "";
        private string               _lastNotifiedNotificationId  = "";
        private ToolStripMenuItem?   _trayItemAutostart;
        private ToolStripMenuItem?   _trayItemBgUpdates;
        private WatchdogService?     _watchdog;

        public MainWindow()
        {
            InitializeComponent();

            // Метка версии в сайдбаре — раньше был захардкожен литерал "LAUNCHER  2.1",
            // расходившийся с реальной версией сборки (Gap Analysis).
            var assemblyVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            txtLauncherVersionLabel.Text = assemblyVersion != null
                ? $"LAUNCHER  {assemblyVersion.Major}.{assemblyVersion.Minor}"
                : "LAUNCHER";

            logExpander.Expanded += LogExpander_Expanded;
            MotionService.Enabled = Environment.GetEnvironmentVariable("VEN4TOOLS_REDUCE_MOTION") != "1";
            Loaded += (_, _) =>
            {
                MotionService.FadeIn(this);
                MotionService.Pulse(btnLaunchApp, 1.025, 220);
            };
            _isUiTestMode = Environment.GetEnvironmentVariable("VEN4TOOLS_UI_TEST") == "1";

            string appData = _isUiTestMode
                ? Environment.GetEnvironmentVariable("VEN4TOOLS_UI_TEST_ROOT")
                    ?? Path.Combine(Path.GetTempPath(), "Ven4Tools.UI.Tests")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Ven4Tools");
            Directory.CreateDirectory(appData);
            _settingsPath   = Path.Combine(appData, "launcher_settings.json");
            _dataFolderPath = appData;

            LoadSettings();
            if (_isUiTestMode)
                _minimizeToTray = false;
            if (!_isUiTestMode)
                CreateTrayIcon();

            if (string.IsNullOrEmpty(_installPath))
                _installPath = _isUiTestMode
                    ? Path.Combine(appData, "Install")
                    : AppDomain.CurrentDomain.BaseDirectory;

            _clientPath = Path.Combine(_installPath, "Ven4Tools_Client");
            // Cleanup — ДО CreateDirectory: иначе target уже существует (пустым) к моменту
            // проверки, и восстановление .backup-* при прерванной установке никогда не сработает.
            if (!_isUiTestMode)
                CleanupStaleInstallArtifacts(_clientPath);
            Directory.CreateDirectory(_clientPath);
            txtInstallPath.Text = _isUiTestMode ? @"C:\Ven4Tools-Test\Client" : _clientPath;

            // Начальное состояние главной кнопки выставляем синхронно по наличию клиента
            // на диске — ещё до сетевых проверок, чтобы не было заметного мигания
            // зелёной «Установить Ven4Tools» → «Загрузить/Запустить» (L3). quiet — без
            // записи в журнал, строку добавит последующий LoadVersionsAsync.
            if (!_isUiTestMode)
                CheckExistingClient(quiet: true);

            // Фоновый сервис запускается после установки _clientPath:
            // он читает путь клиента при первой проверке обновлений
            if (!_isUiTestMode)
                StartBackgroundService();

            Loaded += async (s, e) =>
            {
                if (_isUiTestMode)
                    return;

                // Лаунчер запущен не из папки установки — предлагаем установить.
                // Если пользователь согласился и установщик запущен — выходим.
                var installSvc = new LauncherUpdateService(AddLog, _downloadSource);
                if (await installSvc.OfferInstallationAsync())
                {
                    ExitApplication();
                    return;
                }

                if (_startMinimized)
                {
                    // Окно скрыто (автозапуск в трее) — не запускаем установку выбранных
                    // в setup компонентов незаметно. Откладываем до первого показа окна
                    // из трея, где UAC-диалоги и прогресс будут видны пользователю.
                    Hide();
                    _pendingSetupComponents = true;
                }
                else
                {
                    await ProcessSetupComponentRequestsAsync();
                }
                await LoadVersionsAsync();
                await CheckComponentsAutoAsync();

                // Модальные отчёты не показываем поверх скрытого окна (автозапуск в
                // трее) — откладываем до первого показа из трея (см. ShowWindow).
                if (_startMinimized)
                    _pendingStartupReports = true;
                else
                    ShowStartupReports();
            };
        }

        // Показ отложенных модальных отчётов при старте: крэш клиента и неуспешные
        // установки. Вызывается сразу (окно видимо) либо из ShowWindow при первом
        // раскрытии окна из трея (окно было скрыто при автозапуске).
        private void ShowStartupReports()
        {
            var crash = ReadCrashReport();
            if (crash != null && !crash.Reported)
            {
                var win = new CrashReportWindow(crash) { Owner = this };
                win.ShowDialog();
            }
            var failures = ReadInstallFailures();
            if (failures.Count > 0)
            {
                var win = new InstallReportWindow(failures) { Owner = this };
                win.ShowDialog();
            }
        }

        private void LogExpander_Expanded(object sender, RoutedEventArgs e) =>
            MotionService.FadeIn(txtLog, 180);

        private sealed class LauncherSettings
        {
            public bool    MinimizeToTray              { get; set; } = true;
            public string? InstallPath                 { get; set; }
            public bool    BackgroundUpdates           { get; set; } = true;
            public bool    Autostart                   { get; set; }
            public bool    StartMinimized              { get; set; }
            public bool    AutoUpdateClient             { get; set; }
            public DownloadSource DownloadSource        { get; set; } = DownloadSource.Auto;
            public string? LastKnownCdnIp               { get; set; }
            public string? LastNotifiedLauncherVersion { get; set; }
            public string? LastNotifiedClientVersion   { get; set; }
            public string? LastNotifiedNotificationId  { get; set; }
        }
    }
}
