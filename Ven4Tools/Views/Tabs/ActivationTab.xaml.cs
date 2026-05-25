using System;
using System.Diagnostics;
using System.Management;
using System.Windows;
using System.Windows.Controls;
using System.Threading.Tasks;
using Ven4Tools.Models;

namespace Ven4Tools.Views.Tabs
{
    public partial class ActivationTab : UserControl
    {
        private Action? _sessionChangedHandler;

        public event Action<string>? LogMessage;

        public ActivationTab()
        {
            InitializeComponent();

            btnActivateWindows.Click += BtnActivateWindows_Click;
            btnActivateOffice.Click += BtnActivateOffice_Click;
            btnCheckStatus.Click += BtnCheckStatus_Click;
            btnInteractiveMAS.Click += BtnInteractiveMAS_Click;

            _sessionChangedHandler = () => Dispatcher.Invoke(UpdateAuthState);
            Loaded += async (_, _) =>
            {
                UserSession.Changed += _sessionChangedHandler;
                UpdateAuthState();
                await CheckActivationStatusAsync();
            };
            Unloaded += (_, _) => UserSession.Changed -= _sessionChangedHandler;
        }

        private void UpdateAuthState()
        {
            pnlActivationAuth.Visibility = UserSession.IsLoggedIn ? Visibility.Collapsed : Visibility.Visible;
        }

        private static bool IsRunningAsAdmin()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        
        private async Task CheckActivationStatusAsync()
        {
            try
            {
                txtWindowsStatus.Text = "Проверка...";
                txtOfficeStatus.Text = "Проверка...";
                
                await Task.Run(() =>
                {
                    try
                    {
                        using (var searcher = new ManagementObjectSearcher("SELECT LicenseStatus, ApplicationID FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL"))
                        {
                            foreach (var obj in searcher.Get())
                            {
                                int status = Convert.ToInt32(obj["LicenseStatus"]);
                                string appId = obj["ApplicationID"]?.ToString() ?? "";
                                
                                if (appId.Contains("Windows"))
                                {
                                    Dispatcher.Invoke(() =>
                                    {
                                        txtWindowsStatus.Text = status switch
                                        {
                                            1 => "✅ Активирована",
                                            0 => "❌ Не активирована",
                                            _ => "⚠️ Неизвестно"
                                        };
                                        txtWindowsStatus.Foreground = status == 1 ? 
                                            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightGreen) : 
                                            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightCoral);
                                    });
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            txtWindowsStatus.Text = "⚠️ Ошибка";
                            txtWindowsStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Orange);
                            AddLog($"❌ Ошибка проверки Windows: {ex.Message}");
                        });
                    }
                });
                
                await Task.Run(() =>
                {
                    try
                    {
                        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration"))
                        {
                            if (key != null)
                            {
                                var status = key.GetValue("ProductReleaseIds");
                                if (status != null)
                                {
                                    Dispatcher.Invoke(() =>
                                    {
                                        txtOfficeStatus.Text = "✅ Установлен (статус активации проверьте в Office)";
                                        txtOfficeStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightGreen);
                                    });
                                    return;
                                }
                            }
                        }
                        
                        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Office\16.0\Common\Licensing"))
                        {
                            if (key != null)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    txtOfficeStatus.Text = "✅ Office установлен";
                                    txtOfficeStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightGreen);
                                });
                                return;
                            }
                        }
                        
                        Dispatcher.Invoke(() =>
                        {
                            txtOfficeStatus.Text = "❓ Office не обнаружен";
                            txtOfficeStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightCoral);
                        });
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            txtOfficeStatus.Text = "⚠️ Ошибка";
                            txtOfficeStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Orange);
                            AddLog($"❌ Ошибка проверки Office: {ex.Message}");
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка проверки статуса: {ex.Message}");
            }
        }
        
        // Интерактивный режим — открывает окно PowerShell
        private void BtnInteractiveMAS_Click(object sender, RoutedEventArgs e)
        {
            var warn = MessageBox.Show(
                "⚠️ ПРЕДУПРЕЖДЕНИЕ\n\n" +
                "Будет открыт PowerShell с правами администратора.\n" +
                "Запускается скрипт из интернета: get.activated.win\n\n" +
                "Это открытый проект Microsoft Activation Scripts (MAS).\n" +
                "Исходный код: github.com/massgravel/Microsoft-Activation-Scripts\n\n" +
                "Убедитесь, что доверяете источнику. Продолжить?",
                "Активация — внимание",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (warn != MessageBoxResult.Yes) return;

            try
            {
                AddLog("━━━━━━━━━━━━━━━━━━━━━━");
                AddLog("🚀 Запуск интерактивного режима MAS...");
                AddLog("━━━━━━━━━━━━━━━━━━━━━━");
                
                string command = "irm https://get.activated.win | iex";
                
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoExit -Command \"{command}\"",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                
                Process.Start(psi);
                AddLog("✅ Интерактивный MAS запущен в отдельном окне");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка запуска: {ex.Message}");
                MessageBox.Show($"Ошибка запуска: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        // Тихая активация — без окна, вывод в лог
        private async void BtnActivateWindows_Click(object sender, RoutedEventArgs e)
        {
            string parameter = rdbHwid.IsChecked == true ? "/hwid" : "/kms38";
            await RunSilentActivationAsync(parameter, "Windows");
        }
        
        private async void BtnActivateOffice_Click(object sender, RoutedEventArgs e)
        {
            string parameter = rdbOhook.IsChecked == true ? "/ohook" : "/kms";
            await RunSilentActivationAsync(parameter, "Office");
        }
        
        private async Task RunSilentActivationAsync(string parameter, string product)
        {
            var warn = MessageBox.Show(
                $"⚠️ ПРЕДУПРЕЖДЕНИЕ\n\n" +
                $"Активация {product} выполняется через скрипт из интернета: get.activated.win\n\n" +
                "Это открытый проект Microsoft Activation Scripts (MAS).\n" +
                "Исходный код: github.com/massgravel/Microsoft-Activation-Scripts\n\n" +
                "Продолжить?",
                $"Активация {product} — внимание",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (warn != MessageBoxResult.Yes) return;

            if (!IsRunningAsAdmin())
            {
                AddLog("⚠️ Требуются права администратора.");
                MessageBox.Show(
                    "Для активации требуются права администратора.\n\nПерезапустите Ven4Tools от имени администратора.",
                    "Недостаточно прав",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                AddLog("━━━━━━━━━━━━━━━━━━━━━━");
                AddLog($"🔑 Активация {product} с параметром {parameter}...");
                AddLog("━━━━━━━━━━━━━━━━━━━━━━");
                
                string command = $"& ([ScriptBlock]::Create((curl.exe -s https://get.activated.win | Out-String))) {parameter}";
                
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-ExecutionPolicy Bypass -Command \"{command}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using (var process = new Process { StartInfo = psi })
                {
                    var outputBuilder = new System.Text.StringBuilder();
                    var errorBuilder = new System.Text.StringBuilder();
                    
                    process.OutputDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            outputBuilder.AppendLine(e.Data);
                            Dispatcher.Invoke(() => AddLog($"  {e.Data}"));
                        }
                    };
                    
                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            errorBuilder.AppendLine(e.Data);
                            Dispatcher.Invoke(() => AddLog($"⚠️ {e.Data}"));
                        }
                    };
                    
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    
                    await process.WaitForExitAsync();
                    
                    AddLog($"📊 Код завершения: {process.ExitCode}");
                    
                    if (process.ExitCode == 0)
                    {
                        AddLog($"✅ Активация {product} выполнена успешно!");
                        AddLog("━━━━━━━━━━━━━━━━━━━━━━");
                        
                        MessageBox.Show($"Активация {product} выполнена успешно!", "Успех", 
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        AddLog($"❌ Ошибка активации {product}");
                        AddLog("━━━━━━━━━━━━━━━━━━━━━━");
                        
                        MessageBox.Show($"Ошибка активации {product}.\n\nПроверьте подключение к интернету и повторите попытку.", "Ошибка", 
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                
                AddLog($"🔄 Обновление статуса...");
                await CheckActivationStatusAsync();
                AddLog($"✅ Статус обновлён");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка: {ex.Message}");
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private async void BtnCheckStatus_Click(object sender, RoutedEventArgs e)
        {
            btnCheckStatus.IsEnabled = false;
            await CheckActivationStatusAsync();
            btnCheckStatus.IsEnabled = true;
            AddLog("🔄 Статус активации обновлён");
        }
        
        private void AddLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                txtActivationLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                txtActivationLog.ScrollToEnd();
            });
            LogMessage?.Invoke(message);
        }
    }
}
