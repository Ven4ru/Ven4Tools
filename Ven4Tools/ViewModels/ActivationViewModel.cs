using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Ven4Tools.Helpers;
using Ven4Tools.Services;
using Ven4Tools.Views;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Вкладка «Активация» — согласие, статус лицензий Windows/Office, ссылки на
    /// сторонний инструмент активации. Перенесено из code-behind при MVVM-миграции
    /// (2026-08-25, четвёртая вкладка после DebloaterTab/HistoryTab/AboutTab),
    /// поведение не менялось — кроме способа попасть в UI-поток: у ViewModel нет
    /// собственного Dispatcher, используется Application.Current.Dispatcher.
    /// </summary>
    public sealed class ActivationViewModel : ViewModelBase
    {
        private static readonly TimeSpan OfficeCheckTimeout = TimeSpan.FromSeconds(30);

        public Func<Window?>? OwnerWindowProvider { get; set; }

        private bool _consentGiven;
        public bool ConsentGiven
        {
            get => _consentGiven;
            set => SetField(ref _consentGiven, value);
        }

        private string _windowsStatusText = "Проверка...";
        public string WindowsStatusText { get => _windowsStatusText; private set => SetField(ref _windowsStatusText, value); }

        // Цвет статуса до первой реальной проверки. Раньше был захардкожен белым (#FFFFFFFF),
        // из-за чего «Проверка...» становилась невидимой в светлой теме — в XAML вкладки
        // статусы всегда были Foreground="{DynamicResource TextPrimary}". Берём тот же
        // темизированный ресурс; белый остаётся фолбэком, если Application ещё нет
        // (юнит-тесты) или ресурс не задан.
        private Brush _windowsStatusBrush = BrushResolver.Resolve("TextPrimary");
        public Brush WindowsStatusBrush { get => _windowsStatusBrush; private set => SetField(ref _windowsStatusBrush, value); }

        private string _officeStatusText = "Проверка...";
        public string OfficeStatusText { get => _officeStatusText; private set => SetField(ref _officeStatusText, value); }

        private Brush _officeStatusBrush = BrushResolver.Resolve("TextPrimary");
        public Brush OfficeStatusBrush { get => _officeStatusBrush; private set => SetField(ref _officeStatusBrush, value); }

        private bool _isCheckingStatus;
        public bool IsCheckingStatus
        {
            get => _isCheckingStatus;
            private set { if (SetField(ref _isCheckingStatus, value)) CheckStatusCommand.RaiseCanExecuteChanged(); }
        }

        public RelayCommand ActivateWindowsCommand { get; }
        public RelayCommand ActivateOfficeCommand { get; }
        public RelayCommand CheckStatusCommand { get; }

        public ActivationViewModel()
        {
            ActivateWindowsCommand = new RelayCommand(_ => ActivateWindows());
            ActivateOfficeCommand = new RelayCommand(_ => ActivateOffice());
            CheckStatusCommand = RelayCommand.FromAsync(async _ => await RunCheckStatusAsync(), _ => !IsCheckingStatus);
        }

        // Открывает сайт и окно-помощник для активации Windows
        private void ActivateWindows()
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://massgrave.dev") { UseShellExecute = true });
                AppLogger.Write("🌐 Открыт сайт для управления лицензией Windows");
                var guide = new MasGuideWindow("Windows") { Owner = OwnerWindowProvider?.Invoke() };
                guide.Show();
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка: {ex.Message}"); }
        }

        // Открывает сайт и окно-помощник для активации Office
        private void ActivateOffice()
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://massgrave.dev") { UseShellExecute = true });
                AppLogger.Write("🌐 Открыт сайт для управления лицензией Office");
                var guide = new MasGuideWindow("Office") { Owner = OwnerWindowProvider?.Invoke() };
                guide.Show();
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка: {ex.Message}"); }
        }

        private async Task RunCheckStatusAsync()
        {
            IsCheckingStatus = true;
            try
            {
                await CheckActivationStatusAsync();
                AppLogger.Write("🔄 Статус активации обновлён");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка: {ex.Message}");
            }
            finally
            {
                IsCheckingStatus = false;
            }
        }

        public async Task CheckActivationStatusAsync()
        {
            try
            {
                WindowsStatusText = "Проверка...";
                OfficeStatusText = "Проверка...";

                await Task.Run(() =>
                {
                    try
                    {
                        using (var searcher = CreateLicensingSearcher())
                        using (var results = searcher.Get())
                        {
                            foreach (ManagementBaseObject obj in results)
                            using (obj)
                            {
                                int status = Convert.ToInt32(obj["LicenseStatus"]);
                                string name = obj["Name"]?.ToString() ?? "";

                                if (name.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                                {
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        WindowsStatusText = status switch
                                        {
                                            1 => "✅ Активирована",
                                            0 => "❌ Не активирована",
                                            _ => "⚠️ Неизвестно"
                                        };
                                        WindowsStatusBrush = status == 1 ?
                                            new SolidColorBrush(Colors.LightGreen) :
                                            new SolidColorBrush(Colors.LightCoral);
                                    });
                                    return;
                                }
                            }
                        }
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            WindowsStatusText = "⚠️ Не обнаружена";
                            WindowsStatusBrush = new SolidColorBrush(Colors.Orange);
                        });
                    }
                    catch (Exception ex)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            WindowsStatusText = "⚠️ Ошибка";
                            WindowsStatusBrush = new SolidColorBrush(Colors.Orange);
                            AppLogger.Write($"❌ Ошибка проверки Windows: {ex.Message}");
                        });
                    }
                });

                await Task.Run(() => CheckOfficeActivationAsync());
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка проверки статуса: {ex.Message}");
            }
        }

        private async Task CheckOfficeActivationAsync()
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
                        FileName = TrustedExecutablePaths.CScriptExe,
                        Arguments = $"//NoLogo \"{osppPath}\" /dstatus",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    // Таймаут: раньше WaitForExit() был без ограничения — зависший OSPP.VBS
                    // (повреждённая установка Office/недоступный KMS-хост) держал вкладку
                    // в «Проверка...» бесконечно, кнопка «Проверить статус» не разблокировалась.
                    string output;
                    using var timeoutCts = new CancellationTokenSource(OfficeCheckTimeout);
                    using (var proc = Process.Start(psi)!)
                    {
                        using var reg = timeoutCts.Token.Register(() =>
                            { try { proc.Kill(entireProcessTree: true); } catch { } });
                        try
                        {
                            output = await proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                            await proc.WaitForExitAsync(timeoutCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            SetOfficeStatusOnUI("⚠️ Проверка не завершилась", null);
                            return;
                        }
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
                using var searcher = CreateLicensingSearcher();
                using var results = searcher.Get();
                foreach (ManagementBaseObject obj in results)
                using (obj)
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
                Application.Current.Dispatcher.Invoke(() =>
                {
                    OfficeStatusText = "⚠️ Ошибка";
                    OfficeStatusBrush = new SolidColorBrush(Colors.Orange);
                    AppLogger.Write($"❌ Ошибка проверки Office: {ex.Message}");
                });
            }
        }

        private void SetOfficeStatusOnUI(string text, bool? isActivated)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                OfficeStatusText = text;
                OfficeStatusBrush = isActivated switch
                {
                    true  => new SolidColorBrush(Colors.LightGreen),
                    false => new SolidColorBrush(Colors.LightCoral),
                    null  => new SolidColorBrush(Colors.Orange)
                };
            });
        }

        // Единый WMI-запрос лицензий (Windows и Office) — используется при проверке
        // статуса активации и в запасном варианте для Office.
        internal static ManagementObjectSearcher CreateLicensingSearcher() =>
            new("SELECT LicenseStatus, Name FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL");
    }
}
