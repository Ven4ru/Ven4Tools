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
        private string _installPath = "";
        
        public MainWindow()
        {
            InitializeComponent();
            btnLaunchApp.Click += BtnLaunchApp_Click;
            
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
            Directory.CreateDirectory(appData);
            _settingsPath = Path.Combine(appData, "launcher_settings.json");
            
            LoadSettings();
            CreateTrayIcon();
            
            if (string.IsNullOrEmpty(_installPath))
            {
                txtInstallPath.Text = "Не выбрана (будет использована папка с лаунчером)";
                _installPath = AppDomain.CurrentDomain.BaseDirectory;
            }
            else
            {
                txtInstallPath.Text = _installPath;
            }
            
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
        
        private void BtnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Выберите папку для установки Ven4Tools";
                dialog.ShowNewFolderButton = true;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    _installPath = dialog.SelectedPath;
                    txtInstallPath.Text = _installPath;
                    SaveSettings();
                    AddLog($"📁 Папка установки изменена: {_installPath}");
                }
            }
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
        
        private async void BtnLaunchApp_Click(object sender, RoutedEventArgs e)
        {
            AddLog("=== ЗАПУСК VEN4TOOLS ===");
            
            string clientPath = Path.Combine(_installPath, "Ven4Tools.exe");
            AddLog($"Проверка пути: {clientPath}");
            
            var updateService = new UpdateService();
            
            if (!File.Exists(clientPath))
            {
                AddLog("📥 Клиент не найден. Скачиваю последнюю версию...");
                
                progressDownload.Value = 0;
                txtDownloadStatus.Text = "Скачивание: 0%";
                
                var progress = new Progress<int>(p => 
                {
                    progressDownload.Value = p;
                    txtDownloadStatus.Text = $"Скачивание: {p}%";
                    AddLog($"Скачивание: {p}%");
                });
                
                bool success = await updateService.DownloadAndExtractClientAsync("2.3.1", _installPath, progress);
                
                if (success)
                {
                    AddLog("✅ Клиент скачан и распакован");
                    txtDownloadStatus.Text = "Готово";
                    clientPath = Path.Combine(_installPath, "Ven4Tools.exe");
                }
                else
                {
                    AddLog("❌ Ошибка скачивания клиента");
                    txtDownloadStatus.Text = "Ошибка";
                    return;
                }
            }
            else
            {
                AddLog("✅ Клиент уже есть в папке установки");
                txtDownloadStatus.Text = "Готов";
            }
            
            if (File.Exists(clientPath))
            {
                AddLog($"🚀 Запуск клиента: {clientPath}");
                
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = clientPath,
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    
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
