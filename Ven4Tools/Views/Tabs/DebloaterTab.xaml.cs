using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Ven4Tools.Views.Tabs
{
    public partial class DebloaterTab : UserControl
    {
        public event Action<string>? LogMessage;

        private readonly List<DebloatItem> _allItems = BuildItems();
        private CancellationTokenSource? _cts;

        public DebloaterTab()
        {
            InitializeComponent();
            Loaded += (_, _) => ApplyFilter();
        }

        // ── Data ─────────────────────────────────────────────────────────────────

        private static List<DebloatItem> BuildItems() => new()
        {
            // ── Apps ──────────────────────────────────────────────────────────────
            new("Xbox Game Bar",           "Microsoft.XboxGamingOverlay",    "app", "safe",     "Оверлей для записи и скриншотов Xbox. Никак не влияет на геймплей."),
            new("Xbox App",                "Microsoft.XboxApp",              "app", "safe",     "Клиент Xbox. Не нужен без Xbox-аккаунта."),
            new("Xbox Identity Provider",  "Microsoft.XboxIdentityProvider", "app", "safe",     "Аутентификация Xbox."),
            new("Xbox TCUI",               "Microsoft.Xbox.TCUI",            "app", "safe",     "Интерфейсы Xbox. Безопасно удалять."),
            new("Xbox Speech To Text",     "Microsoft.XboxSpeechToTextOverlay","app","safe",    "Голосовой ввод Xbox."),
            new("3D Builder",              "Microsoft.3DBuilder",            "app", "safe",     "Редактор 3D-моделей. Почти никем не используется."),
            new("3D Viewer",               "Microsoft.Microsoft3DViewer",    "app", "safe",     "Просмотрщик 3D-файлов."),
            new("Mixed Reality Portal",    "Microsoft.MixedReality.Portal",  "app", "safe",     "VR-портал. Не нужен без шлема."),
            new("Cortana",                 "Microsoft.549981C3F5F10",        "app", "safe",     "Голосовой помощник Cortana."),
            new("Tips",                    "Microsoft.Getstarted",           "app", "safe",     "Подсказки Windows. Назойливые всплывающие советы."),
            new("Get Help",                "Microsoft.GetHelp",              "app", "safe",     "Помощник поддержки Microsoft."),
            new("Office Hub",              "Microsoft.MicrosoftOfficeHub",   "app", "safe",     "Реклама подписки Office 365."),
            new("Solitaire Collection",    "Microsoft.MicrosoftSolitaireCollection","app","safe","Карточные игры с рекламой."),
            new("People",                  "Microsoft.People",               "app", "safe",     "Приложение «Люди» — контакты."),
            new("Print 3D",                "Microsoft.Print3D",              "app", "safe",     "Утилита 3D-печати."),
            new("Skype",                   "Microsoft.SkypeApp",             "app", "safe",     "Skype UWP. Не нужен при использовании десктопной версии."),
            new("To Do",                   "Microsoft.Todos",                "app", "safe",     "Microsoft To Do — приложение задач."),
            new("Feedback Hub",            "Microsoft.WindowsFeedbackHub",   "app", "safe",     "Сбор отзывов для Microsoft."),
            new("Maps",                    "Microsoft.WindowsMaps",          "app", "safe",     "Карты Windows."),
            new("Voice Recorder",          "Microsoft.WindowsSoundRecorder", "app", "safe",     "Запись звука."),
            new("Groove Music",            "Microsoft.ZuneMusic",            "app", "safe",     "Медиаплеер Windows — заменён приложением Media Player."),
            new("Movies & TV",             "Microsoft.ZuneVideo",            "app", "safe",     "Видеоплеер UWP."),
            new("Phone Link",              "Microsoft.YourPhone",            "app", "safe",     "Синхронизация с Android. Можно убрать если не используете."),
            new("Clipchamp",               "Clipchamp.Clipchamp",            "app", "safe",     "Видеоредактор Microsoft."),
            new("Power Automate",          "Microsoft.PowerAutomateDesktop", "app", "safe",     "RPA-инструмент Microsoft."),

            // ── Privacy ───────────────────────────────────────────────────────────
            new("Телеметрия Windows",      "telemetry",         "privacy","moderate","Отключает отправку данных диагностики в Microsoft. HKLM AllowTelemetry=0."),
            new("История активности",      "activity_history",  "privacy","safe",    "Отключает запись действий пользователя. HKLM EnableActivityFeed=0."),
            new("Рекламный идентификатор", "advertising_id",    "privacy","safe",    "Отключает персонализацию рекламы. HKCU AdvertisingInfo\\Enabled=0."),
            new("Советы и предложения",    "content_delivery",  "privacy","safe",    "Отключает авто-установку рекомендуемых приложений."),
            new("Cortana (реестр)",        "cortana_registry",  "privacy","safe",    "Полное отключение Cortana через GPO. HKLM AllowCortana=0."),
            new("Слежение за вводом",      "input_tracking",    "privacy","moderate","Отключает отслеживание рукописного ввода и набора текста."),
            new("Запись диагностики",      "diag_track",        "privacy","moderate","Останавливает и отключает службу DiagTrack (Connected User Experiences)."),

            // ── Services ─────────────────────────────────────────────────────────
            new("DiagTrack",               "svc_diagtrack",     "service","caution","Служба телеметрии Connected User Experiences. Отключение освобождает ресурсы."),
            new("SysMain (Superfetch)",    "svc_sysmain",       "service","moderate","Prefetch-служба. На SSD нет смысла. На HDD может ухудшить произв-ть."),
            new("WAP Push Message",        "svc_dmwappushsvc",  "service","caution","Служба получения push-сообщений. Используется для MDM и отдельных телеметрий."),
        };

        // ── UI helpers ───────────────────────────────────────────────────────────

        private void ApplyFilter()
        {
            string cat = "all";
            if (rbApps.IsChecked == true)     cat = "app";
            if (rbPrivacy.IsChecked == true)  cat = "privacy";
            if (rbServices.IsChecked == true) cat = "service";

            lstDebloat.ItemsSource = cat == "all"
                ? _allItems
                : _allItems.Where(i => i.Category == cat).ToList();
        }

        private void FilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _allItems) item.IsSelected = true;
            ApplyFilter();
        }

        private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _allItems) item.IsSelected = false;
            ApplyFilter();
        }

        // ── Apply ────────────────────────────────────────────────────────────────

        private async void BtnApplyDebloat_Click(object sender, RoutedEventArgs e)
        {
            var selected = _allItems.Where(i => i.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Ничего не выбрано.", "Debloater",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var hasRisky = selected.Any(i => i.Risk is "caution" or "moderate");
            var confirm = MessageBox.Show(
                $"Будет применено {selected.Count} действий.{(hasRisky ? "\n\n⚠️ Среди них есть умеренные/опасные операции." : "")}\n\nПродолжить?",
                "Debloater — подтверждение",
                MessageBoxButton.YesNo,
                hasRisky ? MessageBoxImage.Warning : MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            btnApplyDebloat.IsEnabled = false;
            progressDebloat.Visibility = Visibility.Visible;
            progressDebloat.Value = 0;

            _cts = new CancellationTokenSource();
            int done = 0;

            foreach (var item in selected)
            {
                txtDebloatStatus.Text = $"⚙️ {item.Name}...";
                progressDebloat.Value = (double)done / selected.Count * 100;

                bool ok = await ApplyItemAsync(item);
                LogMessage?.Invoke($"{(ok ? "✅" : "❌")} {item.Name}");
                done++;
            }

            progressDebloat.Value = 100;
            txtDebloatStatus.Text = $"✅ Готово: применено {done} из {selected.Count}";
            btnApplyDebloat.IsEnabled = true;
            _cts.Dispose(); _cts = null;
        }

        private async Task<bool> ApplyItemAsync(DebloatItem item)
        {
            try
            {
                if (item.Category == "app")
                    return await RemoveAppxAsync(item.Id);

                if (item.Category == "privacy")
                    return ApplyPrivacyTweak(item.Id);

                if (item.Category == "service")
                    return await DisableServiceAsync(item.Id);

                return false;
            }
            catch { return false; }
        }

        private async Task<bool> RemoveAppxAsync(string packageName)
        {
            string script = $"Get-AppxPackage -Name '*{packageName}*' | Remove-AppxPackage -ErrorAction SilentlyContinue; " +
                            $"Get-AppxProvisionedPackage -Online | Where-Object DisplayName -like '*{packageName}*' | Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue";
            return await RunPSAsync(script);
        }

        private bool ApplyPrivacyTweak(string tweakId)
        {
            switch (tweakId)
            {
                case "telemetry":
                    SetReg(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0);
                    SetReg(@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 0);
                    return true;
                case "activity_history":
                    SetReg(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0);
                    SetReg(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0);
                    return true;
                case "advertising_id":
                    SetReg(@"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0);
                    return true;
                case "content_delivery":
                    SetReg(@"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", 0);
                    SetReg(@"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", 0);
                    return true;
                case "cortana_registry":
                    SetReg(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0);
                    return true;
                case "input_tracking":
                    SetReg(@"HKCU:\SOFTWARE\Microsoft\Input\TIPC", "Enabled", 0);
                    return true;
                case "diag_track":
                    _ = RunPSAsync("Stop-Service DiagTrack -Force -ErrorAction SilentlyContinue; Set-Service DiagTrack -StartupType Disabled -ErrorAction SilentlyContinue");
                    return true;
                default:
                    return false;
            }
        }

        private async Task<bool> DisableServiceAsync(string tweakId)
        {
            string svcName = tweakId switch
            {
                "svc_diagtrack"     => "DiagTrack",
                "svc_sysmain"       => "SysMain",
                "svc_dmwappushsvc"  => "dmwappushservice",
                _                   => tweakId
            };
            return await RunPSAsync($"Stop-Service {svcName} -Force -ErrorAction SilentlyContinue; Set-Service {svcName} -StartupType Disabled -ErrorAction SilentlyContinue");
        }

        private void SetReg(string path, string name, int value)
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -Command \"If (!(Test-Path '{path}')) {{ New-Item -Path '{path}' -Force | Out-Null }}; Set-ItemProperty -Path '{path}' -Name '{name}' -Value {value}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    var outTask = Task.Run(() => p.StandardOutput.ReadToEnd());
                    var errTask = Task.Run(() => p.StandardError.ReadToEnd());
                    p.WaitForExit(5000);
                }
            }
            catch { }
        }

        private async Task<bool> RunPSAsync(string script)
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                await Task.WhenAll(
                    p.StandardOutput.ReadToEndAsync(),
                    p.StandardError.ReadToEndAsync());
                await p.WaitForExitAsync();
                return p.ExitCode == 0;
            }
            catch { return false; }
        }
    }

    // ── Model ─────────────────────────────────────────────────────────────────────

    public class DebloatItem : INotifyPropertyChanged
    {
        public string Name        { get; }
        public string Id          { get; }
        public string Category    { get; } // "app", "privacy", "service"
        public string Risk        { get; } // "safe", "moderate", "caution"
        public string Description { get; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public string RiskLabel => Risk switch
        {
            "safe"     => "Безопасно",
            "moderate" => "Умеренно",
            "caution"  => "Осторожно",
            _          => Risk
        };

        public DebloatItem(string name, string id, string category, string risk, string description)
        {
            Name = name; Id = id; Category = category; Risk = risk; Description = description;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
