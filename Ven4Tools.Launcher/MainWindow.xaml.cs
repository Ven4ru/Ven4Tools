using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

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
        private bool                 _detailsPanelOpen = false;
        private bool                 _hasIssues        = false;
        private CancellationTokenSource? _downloadCts;
        private UpdateBackgroundService? _updateService;
        private bool                 _backgroundUpdates = true;
        private bool                 _autostart         = false;
        private bool                 _startMinimized    = false;
        private string               _lastNotifiedLauncherVersion = "";
        private string               _lastNotifiedClientVersion   = "";
        private ToolStripMenuItem?   _trayItemAutostart;
        private ToolStripMenuItem?   _trayItemBgUpdates;
        private WatchdogService?     _watchdog;

        public MainWindow()
        {
            InitializeComponent();

            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ven4Tools");
            Directory.CreateDirectory(appData);
            _settingsPath = Path.Combine(appData, "launcher_settings.json");

            LoadSettings();
            CreateTrayIcon();
            StartBackgroundService();
            SyncCheckboxes();

            if (string.IsNullOrEmpty(_installPath))
                _installPath = AppDomain.CurrentDomain.BaseDirectory;

            _clientPath = Path.Combine(_installPath, "Ven4Tools_Client");
            Directory.CreateDirectory(_clientPath);
            txtInstallPath.Text = _clientPath;

            Loaded += async (s, e) =>
            {
                if (_startMinimized) Hide();
                await LoadVersionsAsync();
                await CheckComponentsAutoAsync();

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
            };
        }

        private sealed class LauncherSettings
        {
            public bool    MinimizeToTray              { get; set; } = true;
            public string? InstallPath                 { get; set; }
            public bool    BackgroundUpdates           { get; set; } = true;
            public bool    Autostart                   { get; set; }
            public bool    StartMinimized              { get; set; }
            public string? LastNotifiedLauncherVersion { get; set; }
            public string? LastNotifiedClientVersion   { get; set; }
        }
    }
}
