using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    public partial class MainWindow : Window
    {
        private NotifyIcon? _notifyIcon;
        private bool _minimizeToTray = true;
        private string _settingsPath;
        private string _installPath = "";
        private string _clientPath = "";
        private List<ClientVersionInfo> _availableVersions = new();
        private ClientVersionInfo? _selectedVersion;
        private bool _detailsPanelOpen = false;
        private bool _hasIssues = false;
        private CancellationTokenSource? _downloadCts;
        private UpdateBackgroundService? _updateService;
        private bool _backgroundUpdates = true;
        private bool _autostart = false;
        private bool _startMinimized = false;
        private string _lastNotifiedLauncherVersion = "";
        private string _lastNotifiedClientVersion = "";
        private ToolStripMenuItem? _trayItemAutostart;
        private ToolStripMenuItem? _trayItemBgUpdates;
        
        public MainWindow()
        {
            InitializeComponent();
            
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
            Directory.CreateDirectory(appData);
            _settingsPath = Path.Combine(appData, "launcher_settings.json");

            LoadSettings();
            CreateTrayIcon();
            StartBackgroundService();
            SyncCheckboxes();

            if (string.IsNullOrEmpty(_installPath))
            {
                _installPath = AppDomain.CurrentDomain.BaseDirectory;
            }

            // Создаём папку для клиента
            _clientPath = Path.Combine(_installPath, "Ven4Tools_Client");
            Directory.CreateDirectory(_clientPath);
            txtInstallPath.Text = _clientPath;

            Loaded += async (s, e) =>
            {
                if (_startMinimized) Hide();
                await LoadVersionsAsync();
                await CheckComponentsAutoAsync();
            };
        }
        
        private sealed class LauncherSettings
        {
            public bool MinimizeToTray { get; set; } = true;
            public string? InstallPath { get; set; }
            public bool BackgroundUpdates { get; set; } = true;
            public bool Autostart { get; set; }
            public bool StartMinimized { get; set; }
            public string? LastNotifiedLauncherVersion { get; set; }
            public string? LastNotifiedClientVersion { get; set; }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<LauncherSettings>(json);
                    if (settings != null)
                    {
                        _minimizeToTray = settings.MinimizeToTray;
                        _installPath = settings.InstallPath ?? "";
                        _backgroundUpdates = settings.BackgroundUpdates;
                        _autostart = settings.Autostart;
                        _startMinimized = settings.StartMinimized;
                        _lastNotifiedLauncherVersion = settings.LastNotifiedLauncherVersion ?? "";
                        _lastNotifiedClientVersion = settings.LastNotifiedClientVersion ?? "";
                    }
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                var settings = new
                {
                    MinimizeToTray = _minimizeToTray,
                    InstallPath = _installPath,
                    BackgroundUpdates = _backgroundUpdates,
                    Autostart = _autostart,
                    StartMinimized = _startMinimized,
                    LastNotifiedLauncherVersion = _lastNotifiedLauncherVersion,
                    LastNotifiedClientVersion = _lastNotifiedClientVersion
                };
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(settings, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(_settingsPath, json);
            }
            catch { }
        }
        
        private async Task LoadVersionsAsync()
        {
            try
            {
                AddLog("🔍 Загрузка списка версий с GitHub...");
                using var gitHubService = new GitHubService();

                var (releases, error) = await gitHubService.GetAllReleasesWithError();

                if (error != null)
                {
                    AddLog($"❌ {error}");
                    return;
                }

                AddLog($"📦 Найдено релизов: {releases.Count}");

                _availableVersions = new List<ClientVersionInfo>();
                foreach (var release in releases)
                {
                    var version = release.tag_name?.TrimStart('v');
                    if (string.IsNullOrEmpty(version)) continue;

                    var clientAsset = release.assets?.FirstOrDefault(a =>
                        a.name != null &&
                        (a.name.Contains("Client", StringComparison.OrdinalIgnoreCase) ||
                         a.name.Contains("Ven4Tools", StringComparison.OrdinalIgnoreCase)) &&
                        a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                        !a.name.Contains("Launcher", StringComparison.OrdinalIgnoreCase));

                    if (clientAsset != null)
                    {
                        AddLog($"   ✅ {version} → {clientAsset.name}");
                        _availableVersions.Add(new ClientVersionInfo
                        {
                            Version     = version,
                            DownloadUrl = clientAsset.browser_download_url ?? "",
                            ReleaseDate = release.published_at,
                            ReleaseNotes = release.body,
                            IsLatest    = releases.First() == release,
                            FileSize    = clientAsset.size
                        });
                    }
                    else
                    {
                        var assetNames = release.assets != null
                            ? string.Join(", ", release.assets.Select(a => a.name))
                            : "нет";
                        AddLog($"   ⚠️ {version} — нет подходящего .zip (assets: {assetNames})");
                    }
                }

                _availableVersions.Sort((a, b) =>
                {
                    var parts1 = a.Version.Split('.');
                    var parts2 = b.Version.Split('.');
                    for (int i = 0; i < Math.Max(parts1.Length, parts2.Length); i++)
                    {
                        int n1 = i < parts1.Length && int.TryParse(parts1[i], out var x) ? x : 0;
                        int n2 = i < parts2.Length && int.TryParse(parts2[i], out var y) ? y : 0;
                        if (n1 != n2) return n2.CompareTo(n1);
                    }
                    return 0;
                });

                if (_availableVersions.Any())
                {
                    cmbVersions.ItemsSource  = _availableVersions;
                    cmbVersions.SelectedItem = _availableVersions.FirstOrDefault(v => v.IsLatest);
                    cmbVersions.IsEnabled    = true;
                    AddLog($"✅ Загружено {_availableVersions.Count} версий");
                    CheckExistingClient();
                }
                else
                {
                    AddLog("⚠️ Нет релизов с подходящим .zip-активом (см. детали выше)");
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка загрузки версий: {ex.Message}");
            }
        }
        
        private void CheckExistingClient()
        {
            string clientExe = Path.Combine(_clientPath, "Ven4Tools.exe");
            if (File.Exists(clientExe))
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(clientExe);
                string currentVersion = versionInfo.FileVersion ?? "unknown";
                
                btnLaunchApp.Content = "🚀 Запустить Ven4Tools";
                btnLaunchApp.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212));
                AddLog($"✅ Найден клиент версии {currentVersion}");
            }
            else
            {
                btnLaunchApp.Content = "📥 Загрузить Ven4Tools";
                btnLaunchApp.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 140, 0));
            }
        }
        
private void CmbVersions_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
{
    if (cmbVersions.SelectedItem is ClientVersionInfo version)
    {
        _selectedVersion = version;

        // Заполняем информацию о версии
        if (version.FileSize > 0)
            txtVersionInfo.Text = $"{version.ReleaseDate:dd.MM.yyyy}  ·  {version.FileSize / 1024 / 1024} МБ";
        else
            txtVersionInfo.Text = version.ReleaseDate != default ? $"{version.ReleaseDate:dd.MM.yyyy}" : "Выберите версию";

        // Показываем release notes только если панель открыта
        if (_detailsPanelOpen)
            ShowReleaseNotes(version.ReleaseNotes);

        // Обновляем текст кнопки в зависимости от наличия клиента
        string clientExe = Path.Combine(_clientPath, "Ven4Tools.exe");
        if (File.Exists(clientExe))
        {
            btnLaunchApp.Content = "🚀 Запустить Ven4Tools";
            btnLaunchApp.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212));
        }
        else
        {
            btnLaunchApp.Content = "📥 Загрузить Ven4Tools";
            btnLaunchApp.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 140, 0));
        }
        
        AddLog($"📌 Выбрана версия: {version.Version}");
    }
}
        
        private void ShowReleaseNotes(string? notes)
        {
            fdvReleaseNotes.Document = ParseMarkdown(notes);
        }

        private void BtnChangelog_Click(object sender, RoutedEventArgs e)
        {
            _detailsPanelOpen = !_detailsPanelOpen;
            if (_detailsPanelOpen)
            {
                colDetails.Width = new GridLength(300);
                if (_selectedVersion != null)
                    ShowReleaseNotes(_selectedVersion.ReleaseNotes);
            }
            else
            {
                colDetails.Width = new GridLength(0);
            }
        }

        private void BtnCloseDetails_Click(object sender, RoutedEventArgs e)
        {
            _detailsPanelOpen = false;
            colDetails.Width = new GridLength(0);
        }

        private System.Windows.Documents.FlowDocument ParseMarkdown(string? markdown)
        {
            var doc = new System.Windows.Documents.FlowDocument
            {
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 12,
                PagePadding = new Thickness(4)
            };

            if (string.IsNullOrWhiteSpace(markdown))
            {
                doc.Blocks.Add(new System.Windows.Documents.Paragraph(
                    new System.Windows.Documents.Run("Нет описания для этой версии.")
                    { Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(170, 170, 170)) }));
                return doc;
            }

            var accentBrush   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212));
            var subBrush      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(150, 200, 255));
            var textBrush     = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White);
            var mutedBrush    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(170, 170, 170));

            System.Windows.Documents.List? currentList = null;

            foreach (var rawLine in markdown.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');

                if (line.StartsWith("## "))
                {
                    currentList = null;
                    var para = new System.Windows.Documents.Paragraph
                    {
                        Margin = new Thickness(0, 8, 0, 2),
                        BorderBrush = accentBrush,
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Padding = new Thickness(0, 0, 0, 2)
                    };
                    para.Inlines.Add(new System.Windows.Documents.Run(line.Substring(3))
                        { FontWeight = FontWeights.Bold, FontSize = 14, Foreground = accentBrush });
                    doc.Blocks.Add(para);
                }
                else if (line.StartsWith("### "))
                {
                    currentList = null;
                    var para = new System.Windows.Documents.Paragraph { Margin = new Thickness(0, 6, 0, 2) };
                    para.Inlines.Add(new System.Windows.Documents.Run(line.Substring(4))
                        { FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = subBrush });
                    doc.Blocks.Add(para);
                }
                else if (line.StartsWith("- ") || line.StartsWith("* "))
                {
                    if (currentList == null)
                    {
                        currentList = new System.Windows.Documents.List
                        {
                            MarkerStyle = System.Windows.TextMarkerStyle.Disc,
                            Margin = new Thickness(16, 2, 0, 2),
                            Padding = new Thickness(8, 0, 0, 0)
                        };
                        doc.Blocks.Add(currentList);
                    }
                    var item = new System.Windows.Documents.ListItem();
                    var ip = new System.Windows.Documents.Paragraph { Margin = new Thickness(0) };
                    ip.Inlines.Add(new System.Windows.Documents.Run(line.Substring(2).Trim()) { Foreground = textBrush });
                    item.Blocks.Add(ip);
                    currentList.ListItems.Add(item);
                }
                else if (string.IsNullOrWhiteSpace(line))
                {
                    currentList = null;
                }
                else
                {
                    currentList = null;
                    var para = new System.Windows.Documents.Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                    para.Inlines.Add(new System.Windows.Documents.Run(line) { Foreground = mutedBrush });
                    doc.Blocks.Add(para);
                }
            }

            return doc;
        }
        
        private void BtnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Выберите папку для установки Ven4Tools";
                dialog.ShowNewFolderButton = true;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    _installPath = dialog.SelectedPath;
                    _clientPath = Path.Combine(_installPath, "Ven4Tools_Client");
                    Directory.CreateDirectory(_clientPath);
                    txtInstallPath.Text = _clientPath;
                    SaveSettings();
                    AddLog($"📁 Папка установки изменена: {_clientPath}");
                    
                    // Проверяем, есть ли клиент в новой папке
                    CheckExistingClient();
                }
            }
        }
        
private async Task DownloadVersionAsync(ClientVersionInfo version, CancellationToken token)
{
    if (version == null) return;

    AddLog($"📥 Скачивание клиента {version.Version}...");

    string tempZip = Path.Combine(Path.GetTempPath(), $"Ven4Tools_Client_{version.Version}_{Guid.NewGuid()}.zip");
    string extractPath = Path.Combine(Path.GetTempPath(), $"extract_{Guid.NewGuid()}");

    progressDownload.Value = 0;
    txtDownloadStatus.Text = "Скачивание: 0%";
    btnCancelDownload.Visibility = Visibility.Visible;
    btnLaunchApp.IsEnabled = false;

    try
    {
        using var client = new HttpClient();
        client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools-Launcher");

        using var response = await client.GetAsync(version.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        var bytesRead = 0L;
        var buffer = new byte[81920];

        using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
        {
            using var stream = await response.Content.ReadAsStreamAsync(token);
            int bytes;
            while ((bytes = await stream.ReadAsync(buffer.AsMemory(), token)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, bytes), token);
                bytesRead += bytes;
                if (totalBytes > 0)
                {
                    var percent = (int)((double)bytesRead / totalBytes * 100);
                    progressDownload.Value = percent;
                    txtDownloadStatus.Text = $"Скачивание: {percent}%";
                }
            }
            await fs.FlushAsync(token);
        }

        token.ThrowIfCancellationRequested();
        txtDownloadStatus.Text = "Распаковка...";
        await Task.Delay(1000, token);

        bool extracted = false;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                Directory.CreateDirectory(extractPath);
                System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, extractPath, true);
                extracted = true;
                AddLog($"✅ Распаковано с попытки {attempt}");
                break;
            }
            catch (IOException ex) when (attempt < 5)
            {
                AddLog($"⚠️ Попытка распаковки {attempt}/5: {ex.Message}");
                await Task.Delay(2000, token);
                GC.Collect(); GC.WaitForPendingFinalizers();
            }
        }
        if (!extracted) throw new IOException("Не удалось распаковать архив после 5 попыток");

        token.ThrowIfCancellationRequested();
        txtDownloadStatus.Text = "Копирование файлов...";

        if (Directory.Exists(_clientPath))
        {
            foreach (var file in Directory.GetFiles(_clientPath)) try { File.Delete(file); } catch { }
            foreach (var dir in Directory.GetDirectories(_clientPath)) try { Directory.Delete(dir, true); } catch { }
        }

        var allFiles = Directory.GetFiles(extractPath, "*", SearchOption.AllDirectories);
        int fileCount = 0;
        foreach (var file in allFiles)
        {
            token.ThrowIfCancellationRequested();
            string relativePath = file.Substring(extractPath.Length + 1);
            string targetFile = Path.Combine(_clientPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile, true);
            if (++fileCount % 20 == 0)
                txtDownloadStatus.Text = $"Копирование: {fileCount}/{allFiles.Length} файлов";
        }

        txtDownloadStatus.Text = "Очистка...";
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                if (File.Exists(tempZip)) File.Delete(tempZip);
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                break;
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(1000);
                GC.Collect(); GC.WaitForPendingFinalizers();
            }
        }

        txtDownloadStatus.Text = "Готово";
        progressDownload.Value = 100;
        AddLog($"✅ Клиент {version.Version} скачан и распакован");
        version.IsInstalled = true;

        btnLaunchApp.Content = "🚀 Запустить Ven4Tools";
        btnLaunchApp.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212));

        System.Windows.MessageBox.Show(
            $"Клиент {version.Version} успешно установлен в:\n{_clientPath}",
            "Установка завершена", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    catch (OperationCanceledException)
    {
        txtDownloadStatus.Text = "Отменено";
        progressDownload.Value = 0;
        AddLog("⏹ Загрузка отменена");
        try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        try { if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true); } catch { }
    }
    catch (Exception ex)
    {
        txtDownloadStatus.Text = "Ошибка";
        AddLog($"❌ Ошибка скачивания: {ex.Message}");
        try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        try { if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true); } catch { }
        System.Windows.MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
    }
    finally
    {
        btnCancelDownload.Visibility = Visibility.Collapsed;
        btnCancelDownload.IsEnabled = true;
        btnLaunchApp.IsEnabled = true;
        _downloadCts?.Dispose();
        _downloadCts = null;
    }
}
        
            
private async void BtnLaunchApp_Click(object sender, RoutedEventArgs e)
{
    // Если версия не выбрана — выбираем последнюю
    if (_selectedVersion == null)
    {
        var latest = _availableVersions.FirstOrDefault(v => v.IsLatest);
        if (latest == null)
        {
            AddLog("❌ Нет доступных версий");
            return;
        }
        _selectedVersion = latest;
        cmbVersions.SelectedItem = latest;
    }
    
    string clientExe = Path.Combine(_clientPath, "Ven4Tools.exe");
    
    // Если клиент уже есть — запускаем
    if (File.Exists(clientExe))
    {
        AddLog($"🚀 Запуск Ven4Tools {_selectedVersion.Version}...");
        
        var psi = new ProcessStartInfo
        {
            FileName = clientExe,
            UseShellExecute = true
        };

        try
        {
            Process.Start(psi);
            AddLog($"✅ Клиент запущен");
        }
        catch (Exception ex)
        {
            AddLog($"❌ Ошибка запуска: {ex.Message}");
            System.Windows.MessageBox.Show($"Не удалось запустить клиент: {ex.Message}", "Ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        return;
    }
    
    // Если нет клиента — скачиваем выбранную версию
    AddLog($"📥 Загрузка клиента {_selectedVersion.Version}...");
    _downloadCts = new CancellationTokenSource();
    await DownloadVersionAsync(_selectedVersion, _downloadCts.Token);
}

private void BtnCancelDownload_Click(object sender, RoutedEventArgs e)
{
    _downloadCts?.Cancel();
    btnCancelDownload.IsEnabled = false;
}
private async Task CheckComponentsAutoAsync()
{
    AddLog("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
    AddLog("🔧 Проверка компонентов...");
    _hasIssues = false;

    bool isAdmin = IsRunAsAdmin();
    AddLog($"🔍 Права администратора: {(isAdmin ? "✅ есть" : "⚠️ нет")}");
    if (!isAdmin) _hasIssues = true;

    AddLog("🔍 Winget...");
    var wingetInfo = await CheckWingetWithVersionAsync();
    if (wingetInfo.IsInstalled)
    {
        AddLog($"   ✅ Winget {wingetInfo.Version}");
        if (wingetInfo.IsOutdated) { AddLog("   ⚠️ Доступна новая версия winget"); _hasIssues = true; }
    }
    else
    {
        AddLog("   ❌ Winget не установлен!");
        _hasIssues = true;
    }

    AddLog("🔍 Обновления лаунчера...");
    var currentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "2.3.1";
    using var gitHubServiceCheck = new GitHubService();
    var updateInfo = await gitHubServiceCheck.CheckLauncherUpdate(currentVersion);
    if (updateInfo?.HasUpdate == true)
    {
        AddLog($"   📢 Доступно обновление лаунчера {updateInfo.LatestVersion}");
        Dispatcher.Invoke(() => btnInstallUpdate.Visibility = Visibility.Visible);
        _hasIssues = true;
    }
    else
    {
        AddLog("   ✅ Лаунчер актуален");
        Dispatcher.Invoke(() => btnInstallUpdate.Visibility = Visibility.Collapsed);
    }

    AddLog("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
    if (_hasIssues)
    {
        AddLog("⚠️ Найдены проблемы. Нажмите «Установить компоненты».");
        Dispatcher.Invoke(() => btnInstallMissing.Visibility = Visibility.Visible);
    }
    else
    {
        AddLog("✅ Все компоненты в порядке.");
        Dispatcher.Invoke(() => btnInstallMissing.Visibility = Visibility.Collapsed);
    }
}

private async void BtnInstallMissing_Click(object sender, RoutedEventArgs e)
{
    btnInstallMissing.Visibility = Visibility.Collapsed;
    await CheckComponentsInteractiveAsync();
    // CheckComponentsAutoAsync inside will show/hide buttons based on fresh state
}

private async Task CheckComponentsInteractiveAsync()
{
    AddLog("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
    AddLog("🔧 Устранение проблем...");

    bool isAdmin = IsRunAsAdmin();
    if (!isAdmin)
    {
        var restartResult = System.Windows.MessageBox.Show(
            "Лаунчер запущен без прав администратора.\n\n" +
            "Для корректной работы рекомендуется перезапустить с правами администратора.\n\n" +
            "Перезапустить сейчас?",
            "Требуются права администратора",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (restartResult == MessageBoxResult.Yes) { RestartAsAdmin(); return; }
    }

    var wingetInfo = await CheckWingetWithVersionAsync();
    if (!wingetInfo.IsInstalled)
    {
        var installResult = System.Windows.MessageBox.Show(
            "Winget (Windows Package Manager) не установлен!\n\n" +
            "Winget необходим для установки большинства приложений.\n\n" +
            "Установить winget сейчас?",
            "Требуется winget",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (installResult == MessageBoxResult.Yes)
        {
            await InstallWingetAsync();
            wingetInfo = await CheckWingetWithVersionAsync();
            AddLog(wingetInfo.IsInstalled
                ? $"   ✅ Winget {wingetInfo.Version}"
                : "   ⚠️ Winget всё ещё не найден. Возможно, требуется перезагрузка.");
        }
    }
    else if (wingetInfo.IsOutdated)
    {
        var updateResult = System.Windows.MessageBox.Show(
            $"Ваша версия winget ({wingetInfo.Version}) устарела.\n\n" +
            "Обновить winget сейчас?",
            "Обновление winget",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (updateResult == MessageBoxResult.Yes)
        {
            await InstallWingetAsync();
            wingetInfo = await CheckWingetWithVersionAsync();
            AddLog(wingetInfo.IsInstalled
                ? $"   ✅ Winget {wingetInfo.Version}"
                : "   ⚠️ Winget всё ещё не обновлён. Возможно, требуется перезагрузка.");
        }
    }

    if (btnInstallUpdate.Visibility == Visibility.Visible)
    {
        var updateResult = System.Windows.MessageBox.Show(
            "Доступно обновление лаунчера. Установить сейчас?",
            "Обновление лаунчера",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (updateResult == MessageBoxResult.Yes)
            await InstallUpdateCoreAsync();
    }

    // Re-run silent check to update _hasIssues
    await CheckComponentsAutoAsync();
}

private async Task<(bool IsInstalled, string? Version, bool IsOutdated)> CheckWingetWithVersionAsync()
{
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = "winget.exe",
            Arguments = "--version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        
        using var process = Process.Start(psi);
        if (process == null) return (false, null, false);
        
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            return (false, null, false);
        
        string version = output.Trim().TrimStart('v');
        
        // Получаем последнюю стабильную версию с GitHub
        using var gitHubService = new GitHubService();
        string? latestVersion = await gitHubService.GetLatestWingetVersionAsync();
        
        bool isOutdated = false;
        if (latestVersion != null && Version.TryParse(version, out var current) && Version.TryParse(latestVersion, out var latest))
        {
            isOutdated = current < latest;
        }
        
        return (true, version, isOutdated);
    }
    catch
    {
        return (false, null, false);
    }
}

private void RestartAsAdmin()
{
    var exeName = Process.GetCurrentProcess().MainModule?.FileName;
    if (exeName != null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exeName,
            UseShellExecute = true,
            Verb = "runas"
        };
        try { Process.Start(psi); } catch { }
    }
    _updateService?.Dispose();
    _notifyIcon?.Dispose();
    System.Windows.Application.Current.Shutdown();  // ← полное имя
}

private async Task InstallWingetAsync()
{
    AddLog("📦 Получение информации о winget с GitHub...");

    string tempMsix = Path.Combine(Path.GetTempPath(), "winget_setup.msixbundle");
    string tempVcLibs = Path.Combine(Path.GetTempPath(), "VCLibs.appx");
    string tempUiXaml = Path.Combine(Path.GetTempPath(), "UIXaml.appx");

    Dispatcher.Invoke(() =>
    {
        progressDownload.Value = 0;
        txtDownloadStatus.Text = "Подготовка...";
        btnCancelDownload.Visibility = Visibility.Collapsed;
        btnLaunchApp.IsEnabled = false;
    });

    try
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools-Launcher");
        http.Timeout = TimeSpan.FromMinutes(10);

        // Получаем URL последнего релиза winget
        var json = await http.GetStringAsync("https://api.github.com/repos/microsoft/winget-cli/releases/latest");
        var release = Newtonsoft.Json.Linq.JObject.Parse(json);

        string? msixUrl = release["assets"]?
            .FirstOrDefault(a => a["name"]?.ToString().EndsWith(".msixbundle") == true &&
                                  a["name"]?.ToString().Contains("DesktopAppInstaller") == true)?
            ["browser_download_url"]?.ToString();

        if (msixUrl == null)
        {
            AddLog("❌ Не удалось найти файл установки winget в последнем релизе");
            return;
        }

        // Скачиваем зависимости параллельно с основным файлом
        AddLog("⬇️ Скачивание зависимостей...");
        Dispatcher.Invoke(() => txtDownloadStatus.Text = "Скачивание зависимостей...");

        var vcLibsTask = DownloadFileAsync(http,
            "https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx",
            tempVcLibs);
        var uiXamlTask = DownloadFileAsync(http,
            "https://github.com/microsoft/microsoft-ui-xaml/releases/download/v2.8.6/Microsoft.UI.Xaml.2.8.x64.appx",
            tempUiXaml);

        await Task.WhenAll(vcLibsTask, uiXamlTask);

        // Скачиваем основной пакет winget с прогрессом
        AddLog($"⬇️ Скачивание winget ({msixUrl.Split('/').Last()})...");

        using var resp = await http.GetAsync(msixUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? -1L;
        var read = 0L;
        var buf = new byte[81920];

        using (var fs = new FileStream(tempMsix, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
        using (var stream = await resp.Content.ReadAsStreamAsync())
        {
            int bytes;
            while ((bytes = await stream.ReadAsync(buf.AsMemory())) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, bytes));
                read += bytes;
                if (total > 0)
                {
                    var pct = (int)((double)read / total * 100);
                    Dispatcher.Invoke(() =>
                    {
                        progressDownload.Value = pct;
                        txtDownloadStatus.Text = $"Winget: {pct}%";
                    });
                }
            }
        }

        AddLog("📦 Установка winget...");
        Dispatcher.Invoke(() => txtDownloadStatus.Text = "Установка...");

        // Устанавливаем через PowerShell: сначала зависимости, потом сам winget
        var script = $@"
            $ErrorActionPreference = 'Stop'
            try {{ Add-AppxPackage -Path '{tempVcLibs}' }} catch {{}}
            try {{ Add-AppxPackage -Path '{tempUiXaml}' }} catch {{}}
            Add-AppxPackage -Path '{tempMsix}' -ForceApplicationShutdown
        ".Trim().Replace(Environment.NewLine, "; ");

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc != null)
        {
            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                AddLog($"⚠️ PowerShell: {stderr.Trim()}");
        }

        var result = await CheckWingetWithVersionAsync();
        if (result.IsInstalled)
        {
            AddLog($"✅ Winget {result.Version} успешно установлен");
            Dispatcher.Invoke(() => txtDownloadStatus.Text = "Winget установлен");
        }
        else
        {
            AddLog("⚠️ Winget не найден после установки. Возможно, требуется перезагрузка.");
            Dispatcher.Invoke(() => txtDownloadStatus.Text = "Требуется перезагрузка");

            var reboot = System.Windows.MessageBox.Show(
                "Winget не обнаружен после установки.\n\nПерезагрузить компьютер сейчас?",
                "Требуется перезагрузка", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (reboot == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo("shutdown", "/r /t 10") { UseShellExecute = true });
        }
    }
    catch (Exception ex)
    {
        AddLog($"❌ Ошибка установки winget: {ex.Message}");
        Dispatcher.Invoke(() => txtDownloadStatus.Text = "Ошибка");
    }
    finally
    {
        try { if (File.Exists(tempMsix)) File.Delete(tempMsix); } catch { }
        try { if (File.Exists(tempVcLibs)) File.Delete(tempVcLibs); } catch { }
        try { if (File.Exists(tempUiXaml)) File.Delete(tempUiXaml); } catch { }
        Dispatcher.Invoke(() =>
        {
            progressDownload.Value = 0;
            btnLaunchApp.IsEnabled = true;
        });
    }
}

private static async Task DownloadFileAsync(HttpClient http, string url, string dest)
{
    try
    {
        var data = await http.GetByteArrayAsync(url);
        await File.WriteAllBytesAsync(dest, data);
    }
    catch { }
}

private bool IsRunAsAdmin()
{
    var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    var principal = new System.Security.Principal.WindowsPrincipal(identity);
    return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}
        private void CreateTrayIcon()
        {
            try
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath);
                _notifyIcon = new NotifyIcon
                {
                    Icon = icon ?? System.Drawing.SystemIcons.Application,
                    Visible = true,
                    Text = "Ven4Tools Launcher"
                };

                _trayItemAutostart = new ToolStripMenuItem("Запускать при старте Windows")
                {
                    Checked = GetAutostart(),
                    CheckOnClick = true
                };
                _trayItemAutostart.CheckedChanged += (s, e) =>
                {
                    _autostart = _trayItemAutostart.Checked;
                    SetAutostart(_autostart);
                    SaveSettings();
                    Dispatcher.Invoke(() => chkAutostart.IsChecked = _autostart);
                };

                _trayItemBgUpdates = new ToolStripMenuItem("Проверять обновления в фоне")
                {
                    Checked = _backgroundUpdates,
                    CheckOnClick = true
                };
                _trayItemBgUpdates.CheckedChanged += (s, e) =>
                {
                    _backgroundUpdates = _trayItemBgUpdates.Checked;
                    if (_backgroundUpdates)
                        _updateService?.Start();
                    else
                        _updateService?.Stop();
                    SaveSettings();
                    Dispatcher.Invoke(() => chkBackgroundUpdates.IsChecked = _backgroundUpdates);
                };

                var itemAutostart = _trayItemAutostart;
                var itemBgUpdates = _trayItemBgUpdates;

                var contextMenu = new ContextMenuStrip();
                contextMenu.Items.Add("Показать окно", null, (s, e) => Dispatcher.Invoke(ShowWindow));
                contextMenu.Items.Add("Проверить обновления", null, async (s, e) =>
                {
                    await (_updateService?.CheckNowAsync() ?? Task.CompletedTask);
                    // InvokeAsync<Task> возвращает DispatcherOperation<Task> — .Task.Unwrap() даёт inner Task
                    await Dispatcher.InvokeAsync(async () => await CheckForUpdatesAsync()).Task.Unwrap();
                });
                contextMenu.Items.Add("-");
                contextMenu.Items.Add(itemAutostart);
                contextMenu.Items.Add(itemBgUpdates);
                contextMenu.Items.Add("-");
                contextMenu.Items.Add("Выход", null, (s, e) => ExitApplication());

                _notifyIcon.ContextMenuStrip = contextMenu;
                _notifyIcon.DoubleClick += (s, e) => Dispatcher.Invoke(ShowWindow);
                _notifyIcon.BalloonTipClicked += (s, e) => Dispatcher.Invoke(ShowWindow);
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ Ошибка создания иконки в трее: {ex.Message}");
            }
        }

        private void StartBackgroundService()
        {
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var launcherVersion = ver != null
                ? $"{ver.Major}.{ver.Minor}.{ver.Build}"
                : "2.3.2";

            _updateService = new UpdateBackgroundService(launcherVersion, () => _clientPath)
            {
                LastNotifiedLauncherVersion = _lastNotifiedLauncherVersion,
                LastNotifiedClientVersion = _lastNotifiedClientVersion
            };

            _updateService.UpdateAvailable += OnUpdateAvailable;

            if (_backgroundUpdates)
                _updateService.Start();
        }

        private void SyncCheckboxes()
        {
            chkBackgroundUpdates.IsChecked = _backgroundUpdates;
            chkStartMinimized.IsChecked = _startMinimized;
            chkAutostart.IsChecked = _autostart;
        }

        private void ChkBackgroundUpdates_Click(object sender, RoutedEventArgs e)
        {
            _backgroundUpdates = chkBackgroundUpdates.IsChecked == true;
            if (_trayItemBgUpdates != null) _trayItemBgUpdates.Checked = _backgroundUpdates;
            if (_backgroundUpdates)
                _updateService?.Start();
            else
                _updateService?.Stop();
            SaveSettings();
        }

        private void ChkStartMinimized_Click(object sender, RoutedEventArgs e)
        {
            _startMinimized = chkStartMinimized.IsChecked == true;
            SaveSettings();
        }

        private void ChkAutostart_Click(object sender, RoutedEventArgs e)
        {
            _autostart = chkAutostart.IsChecked == true;
            if (_trayItemAutostart != null) _trayItemAutostart.Checked = _autostart;
            SetAutostart(_autostart);
            SaveSettings();
        }

        private void OnUpdateAvailable(string type, UpdateInfo info)
        {
            // Все операции — на UI-потоке: и запись полей, и SaveSettings, и UI-обновления.
            // Это устраняет race condition с ThreadPool-потоком таймера.
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (type == "launcher") _lastNotifiedLauncherVersion = info.LatestVersion ?? "";
                    else _lastNotifiedClientVersion = info.LatestVersion ?? "";
                    SaveSettings();

                    string title = type == "launcher"
                        ? $"Обновление лаунчера {info.LatestVersion}"
                        : $"Новая версия Ven4Tools {info.LatestVersion}";

                    string notes = info.ReleaseNotes ?? "Подробности — в окне лаунчера.";
                    notes = System.Text.RegularExpressions.Regex.Replace(notes, @"[#*`\-]", "").Trim();
                    if (notes.Length > 250) notes = notes.Substring(0, 247) + "...";

                    AddLog($"🔔 {title}");
                    AddLog($"   {notes.Replace('\n', ' ')}");

                    _notifyIcon?.ShowBalloonTip(
                        8000,
                        title,
                        $"v{info.CurrentVersion} → v{info.LatestVersion}\n\n{notes}",
                        ToolTipIcon.Info);

                    if (type == "launcher")
                        btnInstallUpdate.Visibility = Visibility.Visible;
                });
            }
            catch { } // Dispatcher может быть выключен при завершении приложения
        }

        private static bool GetAutostart()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
                return key?.GetValue("Ven4Tools.Launcher") != null;
            }
            catch { return false; }
        }

        private static void SetAutostart(bool enable)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
                if (key == null) return;

                if (enable)
                {
                    string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    if (string.IsNullOrEmpty(exe)) return; // single-file publish — MainModule может быть null
                    key.SetValue("Ven4Tools.Launcher", $"\"{exe}\"");
                }
                else
                {
                    key.DeleteValue("Ven4Tools.Launcher", throwOnMissingValue: false);
                }
            }
            catch { }
        }
        
        private void ShowWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }
        
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }
        
        private void ExitApplication()
        {
            _updateService?.Dispose();
            _notifyIcon?.Dispose();
            System.Windows.Application.Current.Shutdown();
        }
        
        private async Task CheckForUpdatesAsync()
        {
            try
            {
                AddLog("🔍 Проверка обновлений лаунчера...");
                btnCheckUpdates.IsEnabled = false;
                
                using var gitHubService = new GitHubService();
                var currentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "2.3.1";
                var updateInfo = await gitHubService.CheckLauncherUpdate(currentVersion);
                
                if (updateInfo != null && updateInfo.HasUpdate)
                {
                    AddLog($"📢 Найдено обновление лаунчера: {updateInfo.LatestVersion}");
                    AddLog($"📝 {updateInfo.ReleaseNotes}");
                    btnInstallUpdate.Visibility = Visibility.Visible;
                }
                else
                {
                    AddLog("✅ У вас последняя версия лаунчера");
                    btnInstallUpdate.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка проверки обновлений: {ex.Message}");
            }
            finally
            {
                btnCheckUpdates.IsEnabled = true;
            }
        }
        
        private void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            _ = CheckForUpdatesAsync();
        }
        
        private async void BtnInstallUpdate_Click(object sender, RoutedEventArgs e)
        {
            await InstallUpdateCoreAsync();
        }

        private async Task InstallUpdateCoreAsync()
        {
            try
            {
                AddLog("📥 Начинаем скачивание обновления лаунчера...");
                btnInstallUpdate.Visibility = Visibility.Collapsed;

                var updateService = new UpdateService();
                var result = await updateService.DownloadAndInstallUpdateAsync();

                if (result)
                {
                    // Скрипт запущен: через 2 сек скопирует новый exe и перезапустит лаунчер.
                    // Выходим, чтобы освободить exe для замены.
                    AddLog("✅ Скрипт обновления запущен. Лаунчер перезапустится через несколько секунд...");
                    await Task.Delay(500);
                    ExitApplication();
                }
                else
                {
                    AddLog("❌ Ошибка при установке обновления");
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка: {ex.Message}");
            }
            finally
            {
                if (IsLoaded)
                    btnInstallUpdate.Visibility = Visibility.Visible;
            }
        }
        
        private async void BtnFindClient_Click(object sender, RoutedEventArgs e)
        {
            btnFindClient.IsEnabled = false;
            AddLog("🔍 Поиск Ven4Tools.exe на диске...");

            try
            {
                var found = await Task.Run(() =>
                {
                    var results = new List<string>();
                    foreach (var root in GetClientSearchRoots())
                    {
                        if (!Directory.Exists(root)) continue;
                        try
                        {
                            foreach (var file in Directory.EnumerateFiles(root, "Ven4Tools.exe", SearchOption.AllDirectories))
                                results.Add(file);
                        }
                        catch { }
                    }
                    return results;
                });

                if (found.Count == 0)
                {
                    AddLog("❌ Ven4Tools.exe не найден в стандартных папках");
                    System.Windows.MessageBox.Show(
                        "Ven4Tools.exe не найден в:\n" +
                        "• Program Files / Program Files (x86)\n" +
                        "• Документы / Documents\n" +
                        "• Загрузки / Downloads\n" +
                        "• Рабочий стол\n\n" +
                        "Воспользуйтесь кнопкой «Выбрать папку» для ручного указания пути.",
                        "Не найдено", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                foreach (var f in found)
                    AddLog($"   📄 {f}");

                string chosen = found[0];
                if (found.Count > 1)
                {
                    var list = string.Join("\n", found.Select((f, i) => $"{i + 1}. {f}"));
                    System.Windows.MessageBox.Show(
                        $"Найдено {found.Count} экземпляра(ов).\nБудет использован первый:\n\n{chosen}\n\nПолный список:\n{list}",
                        "Найдено несколько", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    var result = System.Windows.MessageBox.Show(
                        $"Найдено:\n{chosen}\n\nИспользовать эту папку?",
                        "Ven4Tools найден", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result != MessageBoxResult.Yes) return;
                }

                _clientPath = Path.GetDirectoryName(chosen)!;
                _installPath = Path.GetDirectoryName(_clientPath) ?? _clientPath;
                txtInstallPath.Text = _clientPath;
                SaveSettings();
                AddLog($"✅ Папка установки: {_clientPath}");
                CheckExistingClient();
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка поиска: {ex.Message}");
            }
            finally
            {
                btnFindClient.IsEnabled = true;
            }
        }

        private static IEnumerable<string> GetClientSearchRoots()
        {
            // Program Files (работает на любом языке системы)
            yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            // Документы (SpecialFolder всегда возвращает правильный локализованный путь)
            yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            // Рабочий стол
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            // Загрузки — нет SpecialFolder, ищем через реестр (надёжнее всего, не зависит от языка)
            string? downloads = null;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders");
                downloads = key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}")?.ToString();
            }
            catch { }

            if (!string.IsNullOrEmpty(downloads) && Directory.Exists(downloads))
            {
                yield return downloads;
            }
            else
            {
                // Фоллбэк: оба варианта — английский и русский
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (var name in new[] { "Downloads", "Загрузки" })
                {
                    var path = Path.Combine(userProfile, name);
                    if (Directory.Exists(path)) yield return path;
                }
            }
        }

        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            ExitApplication();
        }
        
        private void AddLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                txtLog.ScrollToEnd();
            });
        }
    }
}