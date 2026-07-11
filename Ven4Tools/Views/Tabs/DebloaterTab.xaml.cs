using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class DebloaterTab : UserControl
    {
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
            if (lstDebloat == null) return;

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

        // ── Публичный доступ для снапшотов конфигурации ─────────────────────────

        /// <summary>Идентификаторы отмеченных сейчас твиков (для сохранения в снапшот).</summary>
        public IReadOnlyList<string> GetSelectedTweakIds() =>
            _allItems.Where(i => i.IsSelected).Select(i => i.Id).ToList();

        /// <summary>Отмечает в UI ровно те твики, чьи идентификаторы переданы.</summary>
        public void SetSelectedTweakIds(IReadOnlyCollection<string> ids)
        {
            foreach (var item in _allItems)
                item.IsSelected = ids.Contains(item.Id);
            ApplyFilter();
        }

        /// <summary>
        /// Применяет твики по идентификаторам тем же путём, что и обычная кнопка
        /// «Применить» (удаление Appx, реестр, службы). Используется восстановлением
        /// снапшота конфигурации. Неизвестные идентификаторы пропускаются.
        /// </summary>
        public async Task<(int Succeeded, int Total)> ApplyTweaksByIdsAsync(
            IReadOnlyCollection<string> ids,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var items = _allItems.Where(i => ids.Contains(i.Id)).ToList();
            int succeeded = 0;
            foreach (var item in items)
            {
                progress?.Report(item.Name);
                bool ok = await ApplyItemAsync(item, ct);
                AppLogger.Write($"{(ok ? "✅" : "❌")} {item.Name} (из снапшота)");
                if (ok) succeeded++;
            }
            return (succeeded, items.Count);
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

            // try/finally: любое исключение в процессе (зависший PowerShell, сбой
            // сервиса и т.п.) не должно оставить кнопку и прогресс-бар навсегда
            // заблокированными — состояние UI восстанавливается в любом случае.
            try
            {
                // Точка восстановления перед удалением приложений и системными твиками,
                // чтобы можно было откатить изменения при нежелательных последствиях.
                txtDebloatStatus.Text = "🛡️ Создаю точку восстановления...";
                AppLogger.Write("🛡️ Создаю точку восстановления перед дебло́тингом...");
                bool rpOk = await SystemRestoreService.CreateRestorePointAsync("Ven4Tools — перед очисткой системы");
                AppLogger.Write(rpOk
                    ? "✅ Точка восстановления создана"
                    : "⚠️ Точка восстановления не создана");

                // Если точку восстановления создать не удалось — предупреждаем пользователя
                // и даём возможность отказаться: без неё откат изменений штатными
                // средствами Windows будет невозможен.
                if (!rpOk)
                {
                    var proceed = MessageBox.Show(
                        "Не удалось создать точку восстановления системы.\n\n" +
                        "Без неё откатить изменения штатными средствами Windows будет нельзя.\n\n" +
                        "Продолжить без точки восстановления?",
                        "Debloater — нет точки восстановления",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (proceed != MessageBoxResult.Yes)
                    {
                        txtDebloatStatus.Text = "Отменено: точка восстановления не создана";
                        progressDebloat.Visibility = Visibility.Collapsed;
                        return;
                    }
                }

                _cts = new CancellationTokenSource();
                int done = 0;
                int succeeded = 0;

                foreach (var item in selected)
                {
                    txtDebloatStatus.Text = $"⚙️ {item.Name}...";
                    progressDebloat.Value = (double)done / selected.Count * 100;

                    bool ok = await ApplyItemAsync(item, _cts.Token);
                    AppLogger.Write($"{(ok ? "✅" : "❌")} {item.Name}");
                    if (ok) succeeded++;
                    done++;
                }

                progressDebloat.Value = 100;
                txtDebloatStatus.Text = $"✅ Готово: применено {succeeded} из {selected.Count}";
            }
            finally
            {
                btnApplyDebloat.IsEnabled = true;
                _cts?.Dispose(); _cts = null;
            }
        }

        private async Task<bool> ApplyItemAsync(DebloatItem item, CancellationToken ct = default)
        {
            try
            {
                if (item.Category == "app")
                    return await RemoveAppxAsync(item.Id, ct);

                if (item.Category == "privacy")
                    return await ApplyPrivacyTweak(item.Id, ct);

                if (item.Category == "service")
                    return await DisableServiceAsync(item.Id, ct);

                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Дебоатер] Ошибка в ApplyItemAsync [{item.Name}]: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> RemoveAppxAsync(string packageName, CancellationToken ct = default)
        {
            string script = $"Get-AppxPackage -Name '*{packageName}*' | Remove-AppxPackage -ErrorAction SilentlyContinue; " +
                            $"Get-AppxProvisionedPackage -Online | Where-Object DisplayName -like '*{packageName}*' | Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue";
            return await RunPSAsync(script, ct);
        }

        private async Task<bool> ApplyPrivacyTweak(string tweakId, CancellationToken ct = default)
        {
            switch (tweakId)
            {
                case "telemetry":
                {
                    bool a = await SetReg(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, ct);
                    bool b = await SetReg(@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 0, ct);
                    return a && b;
                }
                case "activity_history":
                {
                    bool a = await SetReg(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0, ct);
                    bool b = await SetReg(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, ct);
                    return a && b;
                }
                case "advertising_id":
                    return await SetReg(@"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, ct);
                case "content_delivery":
                {
                    bool a = await SetReg(@"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", 0, ct);
                    bool b = await SetReg(@"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", 0, ct);
                    return a && b;
                }
                case "cortana_registry":
                    return await SetReg(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0, ct);
                case "input_tracking":
                    return await SetReg(@"HKCU:\SOFTWARE\Microsoft\Input\TIPC", "Enabled", 0, ct);
                case "diag_track":
                    return await RunPSAsync("Stop-Service DiagTrack -Force -ErrorAction SilentlyContinue; Set-Service DiagTrack -StartupType Disabled -ErrorAction SilentlyContinue", ct);
                default:
                    return false;
            }
        }

        private async Task<bool> DisableServiceAsync(string tweakId, CancellationToken ct = default)
        {
            string? svcName = tweakId switch
            {
                "svc_diagtrack"     => "DiagTrack",
                "svc_sysmain"       => "SysMain",
                "svc_dmwappushsvc"  => "dmwappushservice",
                _                   => null
            };
            if (svcName == null)
            {
                AppLogger.Write($"[Дебоатер] Неизвестный tweakId: {tweakId}");
                return false;
            }
            return await RunPSAsync($"Stop-Service {svcName} -Force -ErrorAction SilentlyContinue; Set-Service {svcName} -StartupType Disabled -ErrorAction SilentlyContinue", ct);
        }

        private static async Task<bool> SetReg(string path, string name, int value, CancellationToken ct = default)
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
                if (p == null) return false;

                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();

                // Тайм-аут 5 секунд: запись в реестр не должна блокировать процесс надолго.
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await p.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(true); } catch { }
                    AppLogger.Write($"[Дебоатер] SetReg: тайм-аут или отмена [{path}\\{name}]");
                    return false;
                }

                await Task.WhenAll(outTask, errTask);
                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Дебоатер] Ошибка SetReg [{path}\\{name}]: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> RunPSAsync(string script, CancellationToken ct = default)
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

                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();

                // Тайм-аут: Remove-AppxPackage/Remove-AppxProvisionedPackage и
                // операции со службами умеют зависать. Без ограничения весь цикл
                // «Применить» блокировался бы навсегда без обратной связи. По образцу
                // SetReg, но с более щедрым лимитом под удаление Appx-пакетов.
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));
                try
                {
                    await p.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    AppLogger.Write("[Дебоатер] RunPSAsync: тайм-аут или отмена — процесс PowerShell завершён принудительно");
                    return false;
                }

                await Task.WhenAll(outTask, errTask);
                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Дебоатер] Ошибка RunPSAsync: {ex.Message}");
                return false;
            }
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
