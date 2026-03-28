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
        
        public MainWindow()
        {
            InitializeComponent();
            
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
            Directory.CreateDirectory(appData);
            _settingsPath = Path.Combine(appData, "launcher_settings.json");

            LoadSettings();
            CreateTrayIcon();
            
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
                    }
                }
            }
            catch { }
        }
        
        private void SaveSettings()
        {
            try
            {
                var settings = new { MinimizeToTray = _minimizeToTray, InstallPath = _installPath };
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
                BtnToggleDetails_Click(null, null);
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
        BtnInstallUpdate_Click(sender, e);  // ← убрали await
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
                
                var contextMenu = new ContextMenuStrip();
                contextMenu.Items.Add("Показать окно", null, (s, e) => ShowWindow());
                contextMenu.Items.Add("Проверить обновления", null, async (s, e) => await CheckForUpdatesAsync());
                contextMenu.Items.Add("-");
                contextMenu.Items.Add("Выход", null, (s, e) => ExitApplication());
                
                _notifyIcon.ContextMenuStrip = contextMenu;
                _notifyIcon.DoubleClick += (s, e) => ShowWindow();
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ Ошибка создания иконки в трее: {ex.Message}");
            }
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
            try
            {
                AddLog("📥 Начинаем скачивание обновления лаунчера...");
                btnInstallUpdate.IsEnabled = false;
                
                var updateService = new UpdateService();
                var result = await updateService.DownloadAndInstallUpdateAsync();
                
                if (result)
                {
                    AddLog("✅ Обновление установлено! Запускаем...");
                    await Task.Delay(1000);
                    BtnLaunchApp_Click(null, null);
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