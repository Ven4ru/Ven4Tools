using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Ven4Tools.Views.Tabs
{
    public partial class SystemTab : UserControl
    {
        private const string TurboBoostRegPath = @"SYSTEM\ControlSet001\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\be337238-0d82-4146-a960-4f3749d470c7";
        
        public event Action<string>? LogMessage;
        
        public SystemTab()
        {
            InitializeComponent();
            
            Loaded += SystemTab_Loaded;
            
            chkAutoStart.Click += ToggleAutoStart;
            btnCopySystemInfo.Click += BtnCopySystemInfo_Click;
            btnOpenLogs.Click += BtnOpenLogs_Click;
            btnClearLogs.Click += BtnClearLogs_Click;
            btnDisableTurboBoost.Click += BtnDisableTurboBoost_Click;
            btnEnableTurboBoost.Click += BtnEnableTurboBoost_Click;
            
            LoadSettings();
        }
        
        private void SystemTab_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSystemInfo();
            LoadAutoStartStatus();
        }
        
        private void LoadSettings()
        {
            try
            {
                var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
                var settingsPath = Path.Combine(appData, "settings.json");
                
                if (File.Exists(settingsPath))
                {
                    var json = File.ReadAllText(settingsPath);
                    dynamic? settings = Newtonsoft.Json.JsonConvert.DeserializeObject(json);
                    if (settings != null)
                    {
                        chkNotifications.IsChecked = settings.Notifications ?? true;
                        chkUpdateNotifications.IsChecked = settings.UpdateNotifications ?? true;
                    }
                }
            }
            catch { }
        }
        
        private void LoadSystemInfo()
        {
            try
            {
                txtOSVersion.Text = Environment.OSVersion.VersionString;
                
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        txtProcessor.Text = obj["Name"]?.ToString()?.Trim() ?? "Неизвестно";
                        break;
                    }
                }
                
                using (var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        long totalMemory = Convert.ToInt64(obj["TotalVisibleMemorySize"]) / 1024 / 1024;
                        txtRAM.Text = $"{totalMemory} ГБ";
                        break;
                    }
                }
                
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                txtAppVersion.Text = version?.ToString() ?? "2.3.0";
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка загрузки информации о системе: {ex.Message}");
            }
        }
        
        private void LoadAutoStartStatus()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"))
                {
                    chkAutoStart.IsChecked = key?.GetValue("Ven4Tools") != null;
                }
            }
            catch
            {
                chkAutoStart.IsChecked = false;
            }
        }
        
        private void ToggleAutoStart(object sender, RoutedEventArgs e)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    
                    if (chkAutoStart.IsChecked == true)
                    {
                        string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                        if (exePath != null)
                        {
                            key.SetValue("Ven4Tools", exePath);
                            AddLog("✅ Ven4Tools добавлен в автозагрузку");
                        }
                    }
                    else
                    {
                        key.DeleteValue("Ven4Tools", false);
                        AddLog("❌ Ven4Tools удалён из автозагрузки");
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ Ошибка настройки автозагрузки: {ex.Message}");
            }
        }
        
        private void BtnCopySystemInfo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string info = $"ОС: {txtOSVersion.Text}\n" +
                              $"Процессор: {txtProcessor.Text}\n" +
                              $"ОЗУ: {txtRAM.Text}\n" +
                              $"Ven4Tools: {txtAppVersion.Text}";
                
                Clipboard.SetText(info);
                AddLog("📋 Информация о системе скопирована в буфер обмена");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка копирования: {ex.Message}");
            }
        }
        
        private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "logs");
                Directory.CreateDirectory(logsPath);
                Process.Start("explorer.exe", logsPath);
                AddLog($"📁 Открыта папка логов: {logsPath}");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка открытия папки логов: {ex.Message}");
            }
        }
        
        private void BtnClearLogs_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Удалить все файлы логов?", "Подтверждение", 
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    string logsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "logs");
                    if (Directory.Exists(logsPath))
                    {
                        foreach (var file in Directory.GetFiles(logsPath))
                        {
                            File.Delete(file);
                        }
                        AddLog("🗑️ Логи очищены");
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"❌ Ошибка очистки логов: {ex.Message}");
                }
            }
        }
        
        private int GetTurboBoostAttributes()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(TurboBoostRegPath))
                {
                    if (key == null) return -1;
                    var val = key.GetValue("Attributes");
                    return val != null ? Convert.ToInt32(val) : -1;
                }
            }
            catch { return -1; }
        }
        
        private void SetTurboBoostAttributes(int value)
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(TurboBoostRegPath, writable: true))
                {
                    if (key == null)
                    {
                        using (var newKey = Registry.LocalMachine.CreateSubKey(TurboBoostRegPath))
                        {
                            newKey.SetValue("Attributes", value, RegistryValueKind.DWord);
                        }
                    }
                    else
                    {
                        key.SetValue("Attributes", value, RegistryValueKind.DWord);
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка установки атрибутов турбобуста: {ex.Message}");
                throw;
            }
        }
        
        private void BtnDisableTurboBoost_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetTurboBoostAttributes(2);
                MessageBox.Show("✅ Турбобуст отключён (Attributes = 2).\n\nПосле перезагрузки в настройках электропитания появится пункт управления.",
                    "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
                AddLog("⚡ Турбобуст отключën");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                AddLog($"❌ Ошибка при отключении турбобуста: {ex.Message}");
            }
        }
        
        private void BtnEnableTurboBoost_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetTurboBoostAttributes(1);
                MessageBox.Show("✅ Турбобуст включён (Attributes = 1).\n\nПосле перезагрузки пункт управления Turbo Boost вернётся к стандартному поведению.",
                    "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
                AddLog("⚡ Турбобуст включён");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                AddLog($"❌ Ошибка при включении турбобуста: {ex.Message}");
            }
        }
        
        private void AddLog(string message)
        {
            LogMessage?.Invoke(message);
        }
    }
}


