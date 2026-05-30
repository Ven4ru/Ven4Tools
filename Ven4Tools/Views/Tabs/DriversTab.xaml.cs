using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Ven4Tools.Views.Tabs
{
    public partial class DriversTab : UserControl
    {
        public event Action<string>? LogMessage;

        private List<DeviceItem> _devices = new();

        // Known devices → winget package IDs
        private static readonly Dictionary<string, string> KnownDrivers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["NVIDIA"]                 = "NVIDIA.GeForceExperience",
            ["AMD Radeon"]             = "AMD.AdrenalinEdition",
            ["AMD Adrenalin"]          = "AMD.AdrenalinEdition",
            ["Intel(R) HD Graphics"]   = "Intel.IntelDriverAndSupportAssistant",
            ["Intel(R) UHD Graphics"]  = "Intel.IntelDriverAndSupportAssistant",
            ["Intel(R) Arc"]           = "Intel.IntelDriverAndSupportAssistant",
            ["Realtek High Definition"] = "Realtek.HighDefinitionAudioCodec",
            ["Realtek PCIe"]           = "Realtek.PCIeCardReader",
            ["Intel(R) Wireless"]      = "Intel.WirelessBluetooth",
            ["Intel(R) Wi-Fi"]         = "Intel.WirelessBluetooth",
            ["Intel(R) Bluetooth"]     = "Intel.WirelessBluetooth",
            ["Qualcomm Atheros"]       = "Qualcomm.AtherosDrivers",
        };

        public DriversTab()
        {
            InitializeComponent();
        }

        private async void BtnScanDrivers_Click(object sender, RoutedEventArgs e)
        {
            btnScanDrivers.IsEnabled = false;
            progressScan.Visibility = Visibility.Visible;
            txtDriverStats.Text = "Сканирование устройств...";
            lstDevices.ItemsSource = null;
            txtDriverLog.Visibility = Visibility.Collapsed;

            _devices = await Task.Run(ScanDevices);

            lstDevices.ItemsSource = _devices;

            int missing  = _devices.Count(d => d.Status == DeviceStatus.Missing);
            int known    = _devices.Count(d => d.Status == DeviceStatus.Missing && d.WingetId != null);
            int total    = _devices.Count;

            txtDriverStats.Text = total == 0
                ? "Устройства не найдены"
                : $"Всего: {total}   |   Проблемных: {missing}   |   Можно установить через winget: {known}";

            progressScan.Visibility = Visibility.Collapsed;
            btnScanDrivers.IsEnabled = true;
            LogMessage?.Invoke($"🔍 Сканирование драйверов: {total} устройств, проблемных: {missing}");
        }

        private static List<DeviceItem> ScanDevices()
        {
            var list = new List<DeviceItem>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, Description, DeviceID, ConfigManagerErrorCode, PNPClass FROM Win32_PnPEntity");

                foreach (ManagementObject obj in searcher.Get())
                {
                    string name      = obj["Name"]?.ToString()?.Trim() ?? "Неизвестное устройство";
                    string devClass  = obj["PNPClass"]?.ToString() ?? "";
                    int    errorCode = Convert.ToInt32(obj["ConfigManagerErrorCode"] ?? 0);

                    // Only surface: devices with driver errors (code != 0) or display/audio/net adapters
                    bool isInteresting = errorCode != 0
                        || devClass.Equals("Display",         StringComparison.OrdinalIgnoreCase)
                        || devClass.Equals("AudioEndpoint",   StringComparison.OrdinalIgnoreCase)
                        || devClass.Equals("Media",           StringComparison.OrdinalIgnoreCase)
                        || devClass.Equals("Net",             StringComparison.OrdinalIgnoreCase)
                        || devClass.Equals("Bluetooth",       StringComparison.OrdinalIgnoreCase)
                        || devClass.Contains("USB",           StringComparison.OrdinalIgnoreCase);

                    if (!isInteresting) continue;

                    var status = errorCode != 0 ? DeviceStatus.Missing : DeviceStatus.OK;
                    string? wingetId = ResolveWingetId(name);

                    list.Add(new DeviceItem
                    {
                        Name        = name,
                        DeviceClass = devClass,
                        Status      = status,
                        WingetId    = wingetId,
                    });
                }
            }
            catch { }

            // Sort: problems first, then by class
            return list
                .OrderByDescending(d => d.Status == DeviceStatus.Missing)
                .ThenBy(d => d.DeviceClass)
                .ThenBy(d => d.Name)
                .ToList();
        }

        private static string? ResolveWingetId(string deviceName)
        {
            foreach (var kvp in KnownDrivers)
            {
                if (deviceName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }
            return null;
        }

        private async void BtnInstallDriver_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not DeviceItem item || item.WingetId == null) return;

            txtDriverLog.Visibility = Visibility.Visible;
            txtDriverLog.AppendText($"\n⬇️ Устанавливаю {item.WingetId}...\n");
            txtDriverLog.ScrollToEnd();

            bool ok = await RunWingetInstallAsync(item.WingetId);
            string result = ok
                ? $"✅ {item.Name} — установлено"
                : $"❌ {item.Name} — ошибка (winget вернул ненулевой код)";

            txtDriverLog.AppendText(result + "\n");
            txtDriverLog.ScrollToEnd();
            LogMessage?.Invoke(result);
        }

        private void BtnInstallSDI_Click(object sender, RoutedEventArgs e)
        {
            // Snappy Driver Installer Origin — free, open source
            const string sdiWingetId = "GlennDelahoy.SnappyDriverInstallerOrigin";
            txtDriverLog.Visibility = Visibility.Visible;
            txtDriverLog.AppendText("\n📦 Запуск установки Snappy Driver Installer Origin...\n");
            txtDriverLog.ScrollToEnd();
            _ = RunWingetInstallAsync(sdiWingetId);
        }

        private static async Task<bool> RunWingetInstallAsync(string packageId)
        {
            try
            {
                var psi = new ProcessStartInfo("winget",
                    $"install --id {packageId} --silent --accept-package-agreements --accept-source-agreements")
                {
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                var stderrTask = p.StandardError.ReadToEndAsync();
                string output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
                await stderrTask;
                return p.ExitCode == 0;
            }
            catch { return false; }
        }
    }

    // ── Model ─────────────────────────────────────────────────────────────────────

    public enum DeviceStatus { OK, Missing }

    public class DeviceItem : INotifyPropertyChanged
    {
        public string       Name        { get; set; } = "";
        public string       DeviceClass { get; set; } = "";
        public DeviceStatus Status      { get; set; }
        public string?      WingetId    { get; set; }

        public string StatusIcon => Status == DeviceStatus.Missing ? "⚠️" : "✅";
        public string StatusText => Status == DeviceStatus.Missing ? "Нет драйвера / ошибка" : "Работает";

        public Visibility WingetBadgeVisibility   => WingetId != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility InstallButtonVisibility => WingetId != null ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
