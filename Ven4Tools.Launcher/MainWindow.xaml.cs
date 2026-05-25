using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
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

            if (_startMinimized)
                Loaded += (s, e) => Hide();
            
            if (string.IsNullOrEmpty(_installPath))
            {
                _installPath = AppDomain.CurrentDomain.BaseDirectory;
            }
            
            // Создаём папку для клиента
            _clientPath = Path.Combine(_installPath, "Ven4Tools_Client");
            Directory.CreateDirectory(_clientPath);
            txtInstallPath.Text = _clientPath;
            
            Loaded += async (s, e) => await LoadVersionsAsync();
            
            // Настраиваем кнопку раздвижения
            btnToggleDetails.Margin = new Thickness(0, 0, 0, 0);
            btnToggleDetails.Padding = new Thickness(0);
        }
        
        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    dynamic? settings = Newtonsoft.Json.JsonConvert.DeserializeObject(json);
                    if (settings != null)
                    {
                        _minimizeToTray = settings.MinimizeToTray ?? true;
                        _installPath = settings.InstallPath ?? "";
                        _backgroundUpdates = settings.BackgroundUpdates ?? true;
                        _autostart = settings.Autostart ?? false;
                        _startMinimized = settings.StartMinimized ?? false;
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
                var gitHubService = new GitHubService();
                _availableVersions = await gitHubService.GetAvailableClientVersions();

                if (_availableVersions.Any())
                {
                    cmbVersions.ItemsSource = _availableVersions;
                    cmbVersions.SelectedItem = _availableVersions.FirstOrDefault(v => v.IsLatest);
                    cmbVersions.IsEnabled = true;
                    AddLog($"✅ Загружено {_availableVersions.Count} версий");
                    
                    // Проверяем, есть ли уже клиент
                    CheckExistingClient();
                }
                else
                {
                    AddLog("⚠️ Не найдено версий клиента на GitHub");
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
        
        // Показываем release notes в раздвижной панели
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
            if (string.IsNullOrEmpty(notes))
            {
                txtReleaseNotes.Text = "Нет описания для этой версии.";
                return;
            }
            
            txtReleaseNotes.Text = notes;
            
            // Автоматически открываем панель, если она закрыта
            if (!_detailsPanelOpen)
            {
                BtnToggleDetails_Click(this, new RoutedEventArgs());
            }
        }
        
        private void BtnToggleDetails_Click(object sender, RoutedEventArgs e)
        {
            _detailsPanelOpen = !_detailsPanelOpen;
            
            if (_detailsPanelOpen)
            {
                colDetails.Width = new GridLength(300);
                btnToggleDetails.Content = "◀";
            }
            else
            {
                colDetails.Width = new GridLength(0);
                btnToggleDetails.Content = "▶";
            }
        }
        
        private void BtnCloseDetails_Click(object sender, RoutedEventArgs e)
        {
            _detailsPanelOpen = false;
            colDetails.Width = new GridLength(0);
            btnToggleDetails.Content = "▶";
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
        
private async Task DownloadVersionAsync(ClientVersionInfo version)
{
    if (version == null) return;
    
    AddLog($"📥 Скачивание клиента {version.Version}...");
    
    // Уникальное имя файла (с GUID)
    string tempZip = Path.Combine(Path.GetTempPath(), $"Ven4Tools_Client_{version.Version}_{Guid.NewGuid()}.zip");
    string extractPath = Path.Combine(Path.GetTempPath(), $"extract_{Guid.NewGuid()}");
    
    progressDownload.Value = 0;
    txtDownloadStatus.Text = "Скачивание: 0%";
    
    try
    {
        // Скачивание с прогрессом
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools-Launcher");
        
        using var response = await client.GetAsync(version.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        
        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        var bytesRead = 0L;
        var buffer = new byte[81920];
        
        // Используем FileShare.None и сразу закрываем
        using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
        {
            using var stream = await response.Content.ReadAsStreamAsync();
            
            int bytes;
            while ((bytes = await stream.ReadAsync(buffer)) > 0)
            {
                await fs.WriteAsync(buffer, 0, bytes);
                bytesRead += bytes;
                
                if (totalBytes > 0)
                {
                    var percent = (int)((double)bytesRead / totalBytes * 100);
                    progressDownload.Value = percent;
                    txtDownloadStatus.Text = $"Скачивание: {percent}%";
                }
            }
            await fs.FlushAsync();
        } // fs закрыт, файл освобождён
        
        txtDownloadStatus.Text = "Распаковка...";
        
        // Даём время на освобождение файла
        await Task.Delay(1000);
        
        // Распаковка с повторными попытками
        bool extracted = false;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                if (Directory.Exists(extractPath))
                    Directory.Delete(extractPath, true);
                Directory.CreateDirectory(extractPath);
                
                System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, extractPath, true);
                extracted = true;
                AddLog($"✅ Распаковано с попытки {attempt}");
                break;
            }
            catch (IOException ex) when (attempt < 5)
            {
                AddLog($"⚠️ Попытка распаковки {attempt}/5: {ex.Message}");
                await Task.Delay(2000);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        
        if (!extracted)
        {
            throw new IOException("Не удалось распаковать архив после 5 попыток");
        }
        
        txtDownloadStatus.Text = "Копирование файлов...";
        
        // Очищаем папку клиента
        if (Directory.Exists(_clientPath))
        {
            foreach (var file in Directory.GetFiles(_clientPath))
                try { File.Delete(file); } catch { }
            foreach (var dir in Directory.GetDirectories(_clientPath))
                try { Directory.Delete(dir, true); } catch { }
        }
        
        // Копируем файлы
        var allFiles = Directory.GetFiles(extractPath, "*", SearchOption.AllDirectories);
        int fileCount = 0;
        
        foreach (var file in allFiles)
        {
            string relativePath = file.Substring(extractPath.Length + 1);
            string targetFile = Path.Combine(_clientPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile, true);
            
            fileCount++;
            if (fileCount % 20 == 0)
            {
                txtDownloadStatus.Text = $"Копирование: {fileCount}/{allFiles.Length} файлов";
            }
        }
        
        txtDownloadStatus.Text = "Очистка...";
        
        // Очистка с повторными попытками
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                if (File.Exists(tempZip)) File.Delete(tempZip);
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                AddLog($"✅ Очистка завершена с попытки {attempt}");
                break;
            }
            catch (IOException) when (attempt < 5)
            {
                AddLog($"⚠️ Попытка очистки {attempt}/5...");
                await Task.Delay(1000);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        
        txtDownloadStatus.Text = "Готово";
        progressDownload.Value = 100;
        
        AddLog($"✅ Клиент {version.Version} скачан и распакован");
        version.IsInstalled = true;
        
        // Меняем кнопку на "Запустить"
        btnLaunchApp.Content = "🚀 Запустить Ven4Tools";
        btnLaunchApp.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212));
        
        System.Windows.MessageBox.Show(
            $"Клиент {version.Version} успешно установлен в:\n{_clientPath}",
            "Установка завершена",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
    catch (Exception ex)
    {
        txtDownloadStatus.Text = "Ошибка";
        AddLog($"❌ Ошибка скачивания: {ex.Message}");
        
        // Пробуем очистить
        try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        try { if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true); } catch { }
        
        System.Windows.MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
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
            UseShellExecute = true,
            Verb = "runas"
        };
        
        try
        {
            Process.Start(psi);
            AddLog($"✅ Клиент запущен");
        }
        catch (Exception ex)
        {
            AddLog($"❌ Ошибка запуска: {ex.Message}");
        }
        return;
    }
    
    // Если нет клиента — скачиваем выбранную версию
    AddLog($"📥 Загрузка клиента {_selectedVersion.Version}...");
    await DownloadVersionAsync(_selectedVersion);
}
private async void BtnCheckComponents_Click(object sender, RoutedEventArgs e)
{
    AddLog("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
    AddLog("🔧 Проверка системных компонентов...");
    
    btnCheckComponents.IsEnabled = false;
    
    try
    {
        // 1. Проверка прав администратора
        bool isAdmin = IsRunAsAdmin();
        AddLog($"🔍 Права администратора: {(isAdmin ? "✅ есть" : "⚠️ нет")}");
        
        if (!isAdmin)
        {
            var restartResult = System.Windows.MessageBox.Show(
                "Лаунчер запущен без прав администратора.\n\n" +
                "Для корректной работы рекомендуется перезапустить с правами администратора.\n\n" +
                "Перезапустить сейчас?",
                "Требуются права администратора",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            
            if (restartResult == MessageBoxResult.Yes)
            {
                RestartAsAdmin();
                return;
            }
        }
        
        // 2. Проверка winget
        AddLog("🔍 Проверка winget...");
        var wingetInfo = await CheckWingetWithVersionAsync();
        
        if (wingetInfo.IsInstalled)
        {
            AddLog($"   ✅ Winget установлен (версия: {wingetInfo.Version})");
            
            if (wingetInfo.IsOutdated)
            {
                AddLog($"   ⚠️ Версия winget устарела! Актуальная: {await new GitHubService().GetLatestWingetVersionAsync()}");
                
                var updateResult = System.Windows.MessageBox.Show(
                    $"Ваша версия winget ({wingetInfo.Version}) устарела.\n\n" +
                    "Рекомендуется обновить для лучшей совместимости.\n\n" +
                    "Открыть страницу загрузки новой версии?",
                    "Обновление winget",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                
                if (updateResult == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://github.com/microsoft/winget-cli/releases",
                        UseShellExecute = true
                    });
                    AddLog("🌐 Открыта страница загрузки winget");
                }
            }
        }
        else
        {
            AddLog("   ❌ Winget НЕ УСТАНОВЛЕН!");
            
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
                
                // Повторная проверка
                wingetInfo = await CheckWingetWithVersionAsync();
                if (wingetInfo.IsInstalled)
                {
                    AddLog($"   ✅ Winget успешно установлен (версия: {wingetInfo.Version})");
                }
                else
                {
                    AddLog("   ⚠️ Winget всё ещё не найден. Возможно, требуется перезагрузка.");
                }
            }
        }
        
        // 3. Проверка .NET Runtime
        AddLog("🔍 Проверка .NET Runtime...");
        bool dotNetInstalled = CheckDotNetInstalled();
        if (dotNetInstalled)
        {
            AddLog("   ✅ .NET Runtime установлен");
        }
        else
        {
            AddLog("   ℹ️ .NET Runtime не обнаружен (клиент self-contained, не требуется)");
        }
        
        // 4. Проверка обновлений лаунчера
AddLog("🔍 Проверка обновлений лаунчера...");
var gitHubService = new GitHubService();
var currentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "2.3.1";
var updateInfo = await gitHubService.CheckLauncherUpdate(currentVersion);

if (updateInfo != null && updateInfo.HasUpdate)
{
    AddLog($"   📢 Найдено обновление лаунчера: {updateInfo.LatestVersion}");
    AddLog($"   📝 {updateInfo.ReleaseNotes}");
    
    var updateResult = System.Windows.MessageBox.Show(
        $"Доступно обновление лаунчера {updateInfo.LatestVersion}!\n\n" +
        $"Текущая версия: {currentVersion}\n\n" +
        "Обновить лаунчер сейчас?",
        "Обновление лаунчера",
        MessageBoxButton.YesNo,
        MessageBoxImage.Question);
    
    if (updateResult == MessageBoxResult.Yes)
    {
        btnInstallUpdate.IsEnabled = true;
        await InstallUpdateCoreAsync();
    }
}
else
{
    AddLog("   ✅ Лаунчер актуален");
}
        
        AddLog("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        AddLog("✅ Проверка компонентов завершена");
        
        // Итоговое сообщение
        string resultMessage = "Проверка компонентов завершена.\n\n";
        resultMessage += $"Права администратора: {(isAdmin ? "✅ есть" : "⚠️ нет")}\n";
        
        if (wingetInfo.IsInstalled)
        {
            resultMessage += $"Winget: ✅ установлен (версия {wingetInfo.Version})";
            if (wingetInfo.IsOutdated) resultMessage += " (есть обновление!)";
        }
        else
        {
            resultMessage += "Winget: ❌ не установлен";
        }
        
        if (updateInfo?.HasUpdate == true)
        {
            resultMessage += $"\n\n📢 Доступно обновление лаунчера {updateInfo.LatestVersion}!";
        }
        
        System.Windows.MessageBox.Show(
            resultMessage,
            "Результат проверки",
            MessageBoxButton.OK,
            (wingetInfo.IsInstalled && isAdmin) ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }
    catch (Exception ex)
    {
        AddLog($"❌ Ошибка проверки: {ex.Message}");
        System.Windows.MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
    }
    finally
    {
        btnCheckComponents.IsEnabled = true;
    }
}

private async Task<bool> CheckWingetInstalledAsync()
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
        if (process == null) return false;
        
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        
        return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
    }
    catch
    {
        return false;
    }
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
        var gitHubService = new GitHubService();
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
    System.Windows.Application.Current.Shutdown();  // ← полное имя
}

private async Task InstallWingetAsync()
{
    AddLog("📦 Установка winget...");
    
    try
    {
        // Открываем страницу последнего релиза
        string releaseUrl = "https://github.com/microsoft/winget-cli/releases/latest";
        
        Process.Start(new ProcessStartInfo
        {
            FileName = releaseUrl,
            UseShellExecute = true
        });
        
        AddLog("🌐 Открыта страница загрузки winget");
        
        System.Windows.MessageBox.Show(
            "Открыта страница последнего релиза winget на GitHub.\n\n" +
            "Скачайте и установите:\n" +
            "• DesktopAppInstaller_x64.msixbundle (для Windows 10/11)\n\n" +
            "После установки нажмите OK для продолжения.",
            "Установка winget",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        
        // Проверяем, установился ли winget
        var wingetInfo = await CheckWingetWithVersionAsync();
        if (wingetInfo.IsInstalled)
        {
            AddLog($"✅ Winget успешно установлен (версия: {wingetInfo.Version})");
        }
        else
        {
            AddLog("⚠️ Winget всё ещё не найден. Возможно, требуется перезагрузка.");
            
            var rebootResult = System.Windows.MessageBox.Show(
                "Winget не обнаружен после установки.\n\n" +
                "Возможно, требуется перезагрузка компьютера.\n\n" +
                "Перезагрузить сейчас?",
                "Требуется перезагрузка",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            
            if (rebootResult == MessageBoxResult.Yes)
            {
                Process.Start("shutdown", "/r /t 10");
            }
        }
    }
    catch (Exception ex)
    {
        AddLog($"❌ Ошибка установки winget: {ex.Message}");
    }
}

private bool CheckDotNetInstalled()
{
    try
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App");
        return key != null;
    }
    catch
    {
        return false;
    }
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
                        btnInstallUpdate.IsEnabled = true;
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
            var result = System.Windows.MessageBox.Show(
                "Выберите действие при закрытии окна:\n\nДа - свернуть в трей\nНет - закрыть программу\nОтмена - оставить окно",
                "Ven4Tools Launcher",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                e.Cancel = true;
                Hide();
                AddLog("📌 Приложение свёрнуто в системный трей");
            }
            else if (result == MessageBoxResult.No)
            {
                _updateService?.Dispose();
                _notifyIcon?.Dispose();
                System.Windows.Application.Current.Shutdown();
            }
            else
            {
                e.Cancel = true;
            }
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
                
                var gitHubService = new GitHubService();
                var currentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "2.3.1";
                var updateInfo = await gitHubService.CheckLauncherUpdate(currentVersion);
                
                if (updateInfo != null && updateInfo.HasUpdate)
                {
                    AddLog($"📢 Найдено обновление лаунчера: {updateInfo.LatestVersion}");
                    AddLog($"📝 {updateInfo.ReleaseNotes}");
                    btnInstallUpdate.IsEnabled = true;
                }
                else
                {
                    AddLog("✅ У вас последняя версия лаунчера");
                    btnInstallUpdate.IsEnabled = false;
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
                btnInstallUpdate.IsEnabled = false;

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
                    btnInstallUpdate.IsEnabled = true;
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