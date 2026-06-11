using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Threading.Tasks;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class ActivationTab : UserControl
    {
        private Action? _sessionChangedHandler;

        public ActivationTab()
        {
            InitializeComponent();

            btnActivateWindows.Click += BtnActivateWindows_Click;
            btnActivateOffice.Click += BtnActivateOffice_Click;
            btnCheckStatus.Click += BtnCheckStatus_Click;
            btnInteractiveMAS.Click += BtnInteractiveMAS_Click;

            btnActivateWindows.IsEnabled = false;
            btnActivateOffice.IsEnabled = false;
            btnInteractiveMAS.IsEnabled = false;

            _sessionChangedHandler = () => Dispatcher.Invoke(UpdateAuthState);
            Loaded += async (_, _) =>
            {
                UserSession.Changed += _sessionChangedHandler;
                UpdateAuthState();
                ApplyAdminState();
                await CheckActivationStatusAsync();
            };
            Unloaded += (_, _) => UserSession.Changed -= _sessionChangedHandler;
        }

        private void UpdateAuthState()
        {
            pnlActivationAuth.Visibility = UserSession.IsLoggedIn ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ChkActivationConsent_Changed(object sender, RoutedEventArgs e)
        {
            bool agreed = chkActivationConsent.IsChecked == true;
            btnActivateWindows.IsEnabled = agreed && IsRunningAsAdmin();
            btnActivateOffice.IsEnabled  = agreed && IsRunningAsAdmin();
            btnInteractiveMAS.IsEnabled  = agreed;
        }

        private void ApplyAdminState()
        {
            if (!IsRunningAsAdmin())
            {
                btnActivateWindows.IsEnabled = false;
                btnActivateOffice.IsEnabled  = false;
                btnActivateWindows.ToolTip = "Требуются права администратора.";
                btnActivateOffice.ToolTip  = "Требуются права администратора.";
                AddLog("⚠️ Тихая активация недоступна — нет прав администратора.");
                AddLog("   Используйте интерактивный режим или перезапустите от имени администратора.");
            }
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
                        using (var searcher = new ManagementObjectSearcher("SELECT LicenseStatus, Name FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL"))
                        {
                            foreach (var obj in searcher.Get())
                            {
                                int status = Convert.ToInt32(obj["LicenseStatus"]);
                                string name = obj["Name"]?.ToString() ?? "";

                                if (name.Contains("Windows", StringComparison.OrdinalIgnoreCase))
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
                                            new SolidColorBrush(Colors.LightGreen) :
                                            new SolidColorBrush(Colors.LightCoral);
                                    });
                                    return;
                                }
                            }
                        }
                        Dispatcher.Invoke(() =>
                        {
                            txtWindowsStatus.Text = "⚠️ Не обнаружена";
                            txtWindowsStatus.Foreground = new SolidColorBrush(Colors.Orange);
                        });
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            txtWindowsStatus.Text = "⚠️ Ошибка";
                            txtWindowsStatus.Foreground = new SolidColorBrush(Colors.Orange);
                            AddLog($"❌ Ошибка проверки Windows: {ex.Message}");
                        });
                    }
                });
                
                await Task.Run(() => CheckOfficeActivation());
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка проверки статуса: {ex.Message}");
            }
        }
        
        private void CheckOfficeActivation()
        {
            try
            {
                // OSPP.VBS — официальный инструмент проверки лицензии Office (2010–2024, 365)
                string[] osppPaths =
                {
                    @"C:\Program Files\Microsoft Office\Office16\OSPP.VBS",
                    @"C:\Program Files (x86)\Microsoft Office\Office16\OSPP.VBS",
                    @"C:\Program Files\Microsoft Office\Office15\OSPP.VBS",
                    @"C:\Program Files (x86)\Microsoft Office\Office15\OSPP.VBS",
                    @"C:\Program Files\Microsoft Office\Office14\OSPP.VBS",
                    @"C:\Program Files (x86)\Microsoft Office\Office14\OSPP.VBS",
                };

                string? osppPath = null;
                foreach (var p in osppPaths)
                    if (File.Exists(p)) { osppPath = p; break; }

                if (osppPath != null)
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "cscript.exe",
                        Arguments = $"//NoLogo \"{osppPath}\" /dstatus",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    string output;
                    using (var proc = Process.Start(psi)!)
                    {
                        output = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit();
                    }

                    bool hasProducts = output.Contains("SKU ID") || output.Contains("LICENSE NAME");
                    if (!hasProducts)
                    {
                        SetOfficeStatusOnUI("❓ Office не обнаружен", null);
                        return;
                    }

                    if (output.Contains("---LICENSED---"))
                        SetOfficeStatusOnUI("✅ Активирован", true);
                    else if (output.Contains("---UNLICENSED---") || output.Contains("NON_GENUINE"))
                        SetOfficeStatusOnUI("❌ Не активирован", false);
                    else if (output.Contains("OOB_GRACE") || output.Contains("NOTIFICATION"))
                        SetOfficeStatusOnUI("⚠️ Пробный период", null);
                    else
                        SetOfficeStatusOnUI("⚠️ Статус неопределён", null);
                    return;
                }

                // Запасной вариант: WMI SoftwareLicensingProduct
                using var searcher = new ManagementObjectSearcher(
                    "SELECT LicenseStatus, Name FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL");

                foreach (var obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString() ?? "";
                    if (name.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (name.Contains("Office", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Microsoft 365", StringComparison.OrdinalIgnoreCase))
                    {
                        int status = Convert.ToInt32(obj["LicenseStatus"]);
                        SetOfficeStatusOnUI(status == 1 ? "✅ Активирован" : "❌ Не активирован", status == 1);
                        return;
                    }
                }

                // Финальный фоллбэк: просто проверяем установлен ли Office
                string[] regPaths =
                {
                    @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration",
                    @"SOFTWARE\Microsoft\Office\16.0\Common\Licensing",
                    @"SOFTWARE\Microsoft\Office\15.0\Common\Licensing",
                };
                bool installed = false;
                foreach (var regPath in regPaths)
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(regPath);
                    if (key != null) { installed = true; break; }
                }

                SetOfficeStatusOnUI(installed ? "⚠️ Статус неизвестен" : "❓ Office не обнаружен", null);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    txtOfficeStatus.Text = "⚠️ Ошибка";
                    txtOfficeStatus.Foreground = new SolidColorBrush(Colors.Orange);
                    AddLog($"❌ Ошибка проверки Office: {ex.Message}");
                });
            }
        }

        private void SetOfficeStatusOnUI(string text, bool? isActivated)
        {
            Dispatcher.Invoke(() =>
            {
                txtOfficeStatus.Text = text;
                txtOfficeStatus.Foreground = isActivated switch
                {
                    true  => new SolidColorBrush(Colors.LightGreen),
                    false => new SolidColorBrush(Colors.LightCoral),
                    null  => new SolidColorBrush(Colors.Orange)
                };
            });
        }

        // Интерактивный режим — открывает окно PowerShell
        private void BtnInteractiveMAS_Click(object sender, RoutedEventArgs e)
        {
            var warn = MessageBox.Show(
                "⚠️ ПРЕДУПРЕЖДЕНИЕ\n\n" +
                "Будет открыт PowerShell с правами администратора.\n" +
                "Запускается скрипт из интернета: get.activated.win\n\n" +
                "Это инструмент активации с открытым кодом.\n" +
                "Исходный код: github.com/massgravel/Microsoft-Activation-Scripts\n\n" +
                "Убедитесь, что доверяете источнику. Продолжить?",
                "Активация — внимание",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (warn != MessageBoxResult.Yes) return;

            try
            {
                AddLog("━━━━━━━━━━━━━━━━━━━━━━");
                AddLog("🚀 Запуск в интерактивном режиме...");
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
                AddLog("✅ Запущен в отдельном окне");
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
                "Это инструмент активации с открытым кодом.\n" +
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
                
                string command = $"& ([ScriptBlock]::Create((iwr -UseB https://get.activated.win | Select-Object -ExpandProperty Content))) {parameter}";
                
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
        
        private static void AddLog(string message) => AppLogger.Write(message);
    }
}
