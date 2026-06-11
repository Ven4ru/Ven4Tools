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
using Ven4Tools.Views;

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

            btnActivateWindows.IsEnabled = false;
            btnActivateOffice.IsEnabled = false;

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

        private void ChkActivationConsent_Changed(object sender, RoutedEventArgs e)
        {
            bool agreed = chkActivationConsent.IsChecked == true;
            btnActivateWindows.IsEnabled = agreed;
            btnActivateOffice.IsEnabled = agreed;
        }

        // Открывает сайт и окно-помощник для активации Windows
        private void BtnActivateWindows_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://massgrave.dev") { UseShellExecute = true });
                AddLog("🌐 Открыт сайт для управления лицензией Windows");
                var guide = new MasGuideWindow("Windows") { Owner = Window.GetWindow(this) };
                guide.Show();
            }
            catch (Exception ex) { AddLog($"❌ Ошибка: {ex.Message}"); }
        }

        // Открывает сайт и окно-помощник для активации Office
        private void BtnActivateOffice_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://massgrave.dev") { UseShellExecute = true });
                AddLog("🌐 Открыт сайт для управления лицензией Office");
                var guide = new MasGuideWindow("Office") { Owner = Window.GetWindow(this) };
                guide.Show();
            }
            catch (Exception ex) { AddLog($"❌ Ошибка: {ex.Message}"); }
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

        private async void BtnCheckStatus_Click(object sender, RoutedEventArgs e)
        {
            btnCheckStatus.IsEnabled = false;
            try
            {
                await CheckActivationStatusAsync();
                AddLog("🔄 Статус активации обновлён");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка: {ex.Message}");
            }
            finally
            {
                btnCheckStatus.IsEnabled = true;
            }
        }

        private static void AddLog(string message) => AppLogger.Write(message);
    }
}
