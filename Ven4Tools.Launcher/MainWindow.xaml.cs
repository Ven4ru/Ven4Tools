using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    public partial class MainWindow : Window
    {
        private NotifyIcon? _notifyIcon;
        private bool _minimizeToTray = true;
        private string _settingsPath;
        
        public MainWindow()
        {
            InitializeComponent();
            
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
            Directory.CreateDirectory(appData);
            _settingsPath = Path.Combine(appData, "launcher_settings.json");
            
            LoadSettings();
            CreateTrayIcon();
            
            Loaded += async (s, e) => await CheckForUpdatesAsync();
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
                    }
                }
            }
            catch { }
        }
        
        private void SaveSettings()
        {
            try
            {
                var settings = new { MinimizeToTray = _minimizeToTray };
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(settings, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(_settingsPath, json);
            }
            catch { }
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
                contextMenu.Items.Add("Запустить Ven4Tools", null, (s, e) => LaunchMainApp());
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
                AddLog("🔍 Проверка обновлений...");
                btnCheckUpdates.IsEnabled = false;
                
                var updateService = new UpdateService();
                var updateInfo = await updateService.CheckForUpdatesAsync();
                
                if (updateInfo != null && updateInfo.HasUpdate)
                {
                    AddLog($"📢 Найдено обновление: {updateInfo.Version}");
                    AddLog($"📝 {updateInfo.ReleaseNotes}");
                    btnInstallUpdate.IsEnabled = true;
                }
                else
                {
                    AddLog("✅ У вас последняя версия");
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
        
        private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            await CheckForUpdatesAsync();
        }
        
        private async void BtnInstallUpdate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AddLog("📥 Начинаем скачивание обновления...");
                btnInstallUpdate.IsEnabled = false;
                
                var updateService = new UpdateService();
                var result = await updateService.DownloadAndInstallUpdateAsync();
                
                if (result)
                {
                    AddLog("✅ Обновление установлено! Запускаем...");
                    await Task.Delay(1000);
                    LaunchMainApp();
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
        
        private async void BtnLaunchApp_Click(object sender, RoutedEventArgs e)
        {
            AddLog("=== ЗАПУСК ВЕН4ТУЛЗ ===");
            await LaunchMainAppAsync();
        }
        
        private async Task LaunchMainAppAsync()
        {
            try
            {
                var updateService = new UpdateService();
                string clientPath = updateService.GetClientPath();
                
                if (string.IsNullOrEmpty(clientPath))
                {
                    AddLog("📥 Клиент не найден. Скачиваю последнюю версию...");
                    
                    var progress = new Progress<int>(p => 
                    {
                        AddLog($"Скачивание: {p}%");
                    });
                    
                    bool success = await updateService.DownloadAndExtractClientAsync("2.3.1", progress);
                    
                    if (success)
                    {
                        AddLog("✅ Клиент скачан и распакован");
                        clientPath = updateService.GetClientPath();
                    }
                    else
                    {
                        AddLog("❌ Ошибка скачивания клиента");
                        return;
                    }
                }
                else
                {
                    AddLog("✅ Клиент уже есть в папке Client");
                }
                
                if (!string.IsNullOrEmpty(clientPath) && File.Exists(clientPath))
                {
                    AddLog($"🚀 Запуск клиента: {clientPath}");
                    
                    var psi = new ProcessStartInfo
                    {
                        FileName = clientPath,
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    
                    try
                    {
                        var process = Process.Start(psi);
                        AddLog($"✅ Клиент запущен (PID: {process?.Id ?? 0})");
                    }
                    catch (Exception ex)
                    {
                        AddLog($"❌ Ошибка запуска: {ex.Message}");
                    }
                }
                else
                {
                    AddLog("❌ Не удалось найти клиент");
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка: {ex.Message}");
            }
        }
        
        private void LaunchMainApp()
        {
            _ = LaunchMainAppAsync();
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
