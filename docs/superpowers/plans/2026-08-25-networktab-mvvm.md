# NetworkTab MVVM Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Перенести логику вкладки «Сеть» (`NetworkTab`, 318 строк) из code-behind в `NetworkViewModel`, оставив `NetworkTab.xaml`/`.xaml.cs` тонкой обёрткой. Пятая вкладка серии MVVM-миграции.

**Architecture:** `NetworkViewModel : INotifyPropertyChanged` + вспомогательный `NetworkCheckResult` (биндуемая строка статуса пинга/сервиса). Команды — `RelayCommand`/`RelayCommand.FromAsync`, тот же паттерн, что `ActivationViewModel`/`HistoryViewModel`. `DiagnosticsService` не меняется.

**Tech Stack:** .NET 8, WPF, xUnit.

## Global Constraints

- Поведение 1:1 с оригиналом, кроме двух явных механических адаптаций:
  1. `this.Dispatcher.Invoke(...)` → `System.Windows.Application.Current.Dispatcher.Invoke(...)`.
  2. Начальный `IconBrush` строк статуса — `(Application.Current?.TryFindResource("TextPrimary") as Brush) ?? Brushes.White` вместо неявного `TextPrimary` из глобального `Style TargetType="TextBlock"` (App.xaml:60). Тот же паттерн, что проверен ревью `ActivationViewModel`.
- `RunAllAsync`'s `finally` обязан явно сбросить ВСЕ индивидуальные busy-флаги (`IsPinging`, `IsCheckingServices`, `IsGettingIp`, `IsCheckingDns` — НЕ `IsResettingNetwork`) в `false`, а не полагаться на условный сброс внутри каждого метода — иначе флаги останутся `true` навсегда после `RunAll`. См. `docs/superpowers/specs/2026-08-25-networktab-mvvm-design.md`, раздел «Важная деталь семантики».
- `x:Name` всех элементов сохраняются без изменений (UI-тесты используют AutomationId кнопок: `btnRunAll`, `btnRefreshAdapters`, `btnPing`, `btnCheckServices`, `btnGetIp`, `btnCheckDns`; `btnResetNetwork` тестами не кликается).
- Никакой статический `IsEnabled` на кнопках не нужен — `CanExecute` + `CommandManager` (подтверждённый вывод предыдущих 4 вкладок).
- Коммиты — на русском, без Claude/AI-атрибуции.
- Ветка `mvvm-networktab` уже создана от `main`, спека закоммичена (`aaeba77`).

---

### Task 1: `NetworkViewModel` + юнит-тесты

**Files:**
- Create: `Ven4Tools/ViewModels/NetworkViewModel.cs`
- Test: `tests/Ven4Tools.Tests/NetworkViewModelTests.cs`

**Interfaces:**
- Consumes: `Ven4Tools.Services.DiagnosticsService` (`GetAdapters()`, `PingHostAsync(string)`, `CheckServiceAsync(string,string)`, `GetPublicIpAsync()`, `CheckDnsAsync(string)`), `Ven4Tools.Services.AdapterInfo`/`PingResult`/`ServiceCheckResult`, `Ven4Tools.Services.ProfileService.Current.ParanoidMode`, `Ven4Tools.Services.AppLogger.Write`, `Ven4Tools.Services.TrustedExecutablePaths.CmdExe`, `Ven4Tools.ViewModels.RelayCommand`/`RelayCommand.FromAsync`.
- Produces: `Ven4Tools.ViewModels.NetworkCheckResult` (публичные свойства `Text`/`IconText`/`IconBrush`, все `INotifyPropertyChanged`), `Ven4Tools.ViewModels.NetworkViewModel` — публичные свойства `Adapters`, `AdaptersEmpty`, `Ping1`..`Ping4`, `Svc1`..`Svc5`, `PublicIpText`, `DnsResultText`, `DnsResultVisible`, `IsBusy`, `IsPinging`, `IsCheckingServices`, `IsGettingIp`, `IsCheckingDns`, `IsResettingNetwork`, `RunAllButtonText`; команды `RunAllCommand`, `RefreshAdaptersCommand`, `PingCommand`, `CheckServicesCommand`, `GetIpCommand`, `CheckDnsCommand`, `ResetNetworkCommand` (все `RelayCommand`); `internal static void SetRow(NetworkCheckResult, string, bool?)`.

- [ ] **Step 1: Создать `Ven4Tools/ViewModels/NetworkViewModel.cs`**

Полное содержимое файла:

```csharp
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Одна строка статуса (пинг-хост или сервис) — текст задержки/статуса,
    /// иконка и её цвет. Общий тип для 4 строк пинга и 5 строк проверки сервисов —
    /// в оригинальном code-behind они обновлялись почти идентичной логикой
    /// (<c>SetPingRow</c> и инлайн-лямбда в <c>RunServicesAsync</c>), здесь эта
    /// логика едина (см. <see cref="NetworkViewModel.SetRow"/>).
    /// </summary>
    public sealed class NetworkCheckResult : INotifyPropertyChanged
    {
        private string _text = "—";
        private string _iconText = "⬜";
        private Brush _iconBrush = ResolveDefaultBrush();

        public string Text
        {
            get => _text;
            set => SetField(ref _text, value);
        }

        public string IconText
        {
            get => _iconText;
            set => SetField(ref _iconText, value);
        }

        public Brush IconBrush
        {
            get => _iconBrush;
            set => SetField(ref _iconBrush, value);
        }

        // Оригинальные txtPingIcon*/txtSvc* не задавали Foreground в XAML явно —
        // цвет наследовался от глобального Style TargetType="TextBlock" (App.xaml:60),
        // который ставит DynamicResource TextPrimary. Явный биндинг на IconBrush
        // заменяет этот неявный канал, поэтому дефолт вычисляется тем же способом,
        // что уже проверен ревью ActivationViewModel — TryFindResource с фолбэком
        // на замороженный (frozen, потокобезопасный) Brushes.White.
        internal static Brush ResolveDefaultBrush() =>
            (Application.Current?.TryFindResource("TextPrimary") as Brush) ?? Brushes.White;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// ViewModel вкладки «Сеть». Логика перенесена из code-behind при MVVM-миграции
    /// (2026-08-25, пятая вкладка после Debloater/History/About/Activation) без
    /// изменения поведения — см. docs/superpowers/specs/2026-08-25-networktab-mvvm-design.md.
    /// </summary>
    public sealed class NetworkViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // ── Адаптеры ─────────────────────────────────────────────────────────

        private IReadOnlyList<AdapterInfo> _adapters = Array.Empty<AdapterInfo>();
        public IReadOnlyList<AdapterInfo> Adapters
        {
            get => _adapters;
            private set => SetField(ref _adapters, value);
        }

        private bool _adaptersEmpty;
        public bool AdaptersEmpty
        {
            get => _adaptersEmpty;
            private set => SetField(ref _adaptersEmpty, value);
        }

        // ── Пинг ─────────────────────────────────────────────────────────────

        public NetworkCheckResult Ping1 { get; } = new();
        public NetworkCheckResult Ping2 { get; } = new();
        public NetworkCheckResult Ping3 { get; } = new();
        public NetworkCheckResult Ping4 { get; } = new();

        // ── Доступность сервисов ─────────────────────────────────────────────

        public NetworkCheckResult Svc1 { get; } = new();
        public NetworkCheckResult Svc2 { get; } = new();
        public NetworkCheckResult Svc3 { get; } = new();
        public NetworkCheckResult Svc4 { get; } = new();
        public NetworkCheckResult Svc5 { get; } = new();

        // ── Внешний IP / DNS ─────────────────────────────────────────────────

        private string _publicIpText = "не определён";
        public string PublicIpText
        {
            get => _publicIpText;
            private set => SetField(ref _publicIpText, value);
        }

        private string _dnsResultText = "";
        public string DnsResultText
        {
            get => _dnsResultText;
            private set => SetField(ref _dnsResultText, value);
        }

        private bool _dnsResultVisible;
        public bool DnsResultVisible
        {
            get => _dnsResultVisible;
            private set => SetField(ref _dnsResultVisible, value);
        }

        // ── Состояние занятости ──────────────────────────────────────────────

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set { SetField(ref _isBusy, value); RaiseAllCanExecuteChanged(); }
        }

        private bool _isPinging;
        public bool IsPinging
        {
            get => _isPinging;
            private set { SetField(ref _isPinging, value); PingCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isCheckingServices;
        public bool IsCheckingServices
        {
            get => _isCheckingServices;
            private set { SetField(ref _isCheckingServices, value); CheckServicesCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isGettingIp;
        public bool IsGettingIp
        {
            get => _isGettingIp;
            private set { SetField(ref _isGettingIp, value); GetIpCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isCheckingDns;
        public bool IsCheckingDns
        {
            get => _isCheckingDns;
            private set { SetField(ref _isCheckingDns, value); CheckDnsCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isResettingNetwork;
        public bool IsResettingNetwork
        {
            get => _isResettingNetwork;
            private set { SetField(ref _isResettingNetwork, value); ResetNetworkCommand.RaiseCanExecuteChanged(); }
        }

        private string _runAllButtonText = "🔍 Запустить полную диагностику";
        public string RunAllButtonText
        {
            get => _runAllButtonText;
            private set => SetField(ref _runAllButtonText, value);
        }

        private void RaiseAllCanExecuteChanged()
        {
            RunAllCommand.RaiseCanExecuteChanged();
            RefreshAdaptersCommand.RaiseCanExecuteChanged();
            PingCommand.RaiseCanExecuteChanged();
            CheckServicesCommand.RaiseCanExecuteChanged();
            GetIpCommand.RaiseCanExecuteChanged();
            CheckDnsCommand.RaiseCanExecuteChanged();
            ResetNetworkCommand.RaiseCanExecuteChanged();
        }

        // ── Команды ──────────────────────────────────────────────────────────

        public RelayCommand RunAllCommand { get; }
        public RelayCommand RefreshAdaptersCommand { get; }
        public RelayCommand PingCommand { get; }
        public RelayCommand CheckServicesCommand { get; }
        public RelayCommand GetIpCommand { get; }
        public RelayCommand CheckDnsCommand { get; }
        public RelayCommand ResetNetworkCommand { get; }

        public NetworkViewModel()
        {
            RunAllCommand          = RelayCommand.FromAsync(_ => RunAllAsync(),     _ => !IsBusy);
            RefreshAdaptersCommand = new RelayCommand(_ => RefreshAdapters(),       _ => !IsBusy);
            PingCommand             = RelayCommand.FromAsync(_ => RunPingAsync(),     _ => !IsBusy && !IsPinging);
            CheckServicesCommand    = RelayCommand.FromAsync(_ => RunServicesAsync(), _ => !IsBusy && !IsCheckingServices);
            GetIpCommand            = RelayCommand.FromAsync(_ => RunGetIpAsync(),    _ => !IsBusy && !IsGettingIp);
            CheckDnsCommand         = RelayCommand.FromAsync(_ => RunDnsAsync(),      _ => !IsBusy && !IsCheckingDns);
            ResetNetworkCommand     = RelayCommand.FromAsync(_ => RunResetNetworkAsync(), _ => !IsBusy && !IsResettingNetwork);
        }

        // ── Полная диагностика ───────────────────────────────────────────────

        private async Task RunAllAsync()
        {
            IsBusy = true;
            RunAllButtonText = "⏳ Диагностика...";
            try
            {
                RefreshAdapters();
                await RunPingAsync();
                await RunServicesAsync();
                await RunGetIpAsync();
                await RunDnsAsync();
            }
            finally
            {
                // Оригинал (SetDiagnosticButtonsEnabled(true) в finally) безусловно
                // возвращал ВСЕ 7 кнопок в IsEnabled=true, даже если внутренние методы
                // не сбросили свой busy-флаг сами (условие "if (!_busy)" внутри них было
                // ложным, пока эта диагностика ещё выполнялась). Явный сброс здесь —
                // точный эквивалент, см. Global Constraints плана.
                IsBusy = false;
                IsPinging = false;
                IsCheckingServices = false;
                IsGettingIp = false;
                IsCheckingDns = false;
                RunAllButtonText = "🔍 Запустить полную диагностику";
            }
        }

        // ── Адаптеры ─────────────────────────────────────────────────────────

        private void RefreshAdapters()
        {
            var adapters = DiagnosticsService.GetAdapters();
            Adapters = adapters;
            AdaptersEmpty = adapters.Count == 0;
            AppLogger.Write($"[Сеть] Адаптеров: {adapters.Count}");
        }

        // ── Пинг ─────────────────────────────────────────────────────────────

        private async Task RunPingAsync()
        {
            IsPinging = true;
            // Параноидальный режим обещает блокировать ВСЕ исходящие запросы, кроме
            // загрузки каталога и установки. Пинг сторонних хостов раскрывает IP —
            // пропускаем, чтобы не нарушать это обещание (внешний IP тут уже гейтится).
            if (ProfileService.Current.ParanoidMode)
            {
                SetRow(Ping1, "отключено", null);
                SetRow(Ping2, "отключено", null);
                SetRow(Ping3, "отключено", null);
                SetRow(Ping4, "отключено", null);
                AppLogger.Write("[Сеть] Пинг пропущен: параноидальный режим");
                if (!IsBusy) IsPinging = false;
                return;
            }
            SetRow(Ping1, "...", null);
            SetRow(Ping2, "...", null);
            SetRow(Ping3, "...", null);
            SetRow(Ping4, "...", null);

            var hosts = new[] { "1.1.1.1", "8.8.8.8", "google.com", "ven4tools.ru" };
            var targets = new[] { Ping1, Ping2, Ping3, Ping4 };

            var tasks = new List<Task>();
            for (int i = 0; i < hosts.Length; i++)
            {
                var host = hosts[i];
                var row = targets[i];
                tasks.Add(Task.Run(async () =>
                {
                    var r = await DiagnosticsService.PingHostAsync(host);
                    Application.Current.Dispatcher.Invoke(() => SetRow(row, r.Display, r.Reachable));
                    AppLogger.Write($"[Сеть] Пинг {host}: {r.Display}");
                }));
            }
            await Task.WhenAll(tasks);
            // Во время полной диагностики флаг разблокирует RunAllAsync в finally.
            if (!IsBusy) IsPinging = false;
        }

        internal static void SetRow(NetworkCheckResult row, string text, bool? ok)
        {
            row.Text = text;
            if (ok == null) { row.IconText = "⬜"; row.IconBrush = Brushes.Gray; return; }
            if (ok == true) { row.IconText = "✅"; row.IconBrush = new SolidColorBrush(Color.FromRgb(74, 222, 128)); }
            else            { row.IconText = "❌"; row.IconBrush = new SolidColorBrush(Colors.LightCoral); }
        }

        // ── Доступность сервисов ─────────────────────────────────────────────

        private async Task RunServicesAsync()
        {
            IsCheckingServices = true;
            var rows = new[] { Svc1, Svc2, Svc3, Svc4, Svc5 };
            // Параноидальный режим: HEAD-запросы к сторонним сервисам раскрывают IP —
            // пропускаем ради соблюдения обещания режима (см. RunPingAsync).
            if (ProfileService.Current.ParanoidMode)
            {
                foreach (var row in rows)
                {
                    row.IconText = "🚫";
                    row.IconBrush = Brushes.Gray;
                    row.Text = "отключено";
                }
                AppLogger.Write("[Сеть] Проверка сервисов пропущена: параноидальный режим");
                if (!IsBusy) IsCheckingServices = false;
                return;
            }
            foreach (var row in rows) { row.IconText = "⏳"; row.IconBrush = Brushes.Gray; }

            var checks = new[]
            {
                ("Google",     "https://www.google.com"),
                ("YouTube",    "https://www.youtube.com"),
                ("Discord",    "https://discord.com"),
                ("Cloudflare", "https://www.cloudflare.com"),
                ("GitHub",     "https://github.com"),
            };

            var tasks = new List<Task>();
            for (int i = 0; i < checks.Length; i++)
            {
                var (name, url) = checks[i];
                var row = rows[i];
                tasks.Add(Task.Run(async () =>
                {
                    var r = await DiagnosticsService.CheckServiceAsync(name, url);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        row.IconText = r.Available ? "✅" : "❌";
                        row.IconBrush = r.Available
                            ? new SolidColorBrush(Color.FromRgb(74, 222, 128))
                            : new SolidColorBrush(Colors.LightCoral);
                        row.Text = r.Available ? $"{r.Ms} мс" : "таймаут";
                    });
                    AppLogger.Write($"[Сеть] {name}: {(r.Available ? "✅" : "❌")} {r.Ms}мс");
                }));
            }
            await Task.WhenAll(tasks);
            if (!IsBusy) IsCheckingServices = false;
        }

        // ── Внешний IP ───────────────────────────────────────────────────────

        private async Task RunGetIpAsync()
        {
            IsGettingIp = true;
            // Параноидальный режим: сам смысл этого запроса — раскрыть внешний IP
            // стороннему echo-сервису, единственная функция которого — раскрыть его.
            if (ProfileService.Current.ParanoidMode)
            {
                PublicIpText = "отключено (параноидальный режим)";
                AppLogger.Write("[Сеть] Запрос внешнего IP пропущен: параноидальный режим");
                if (!IsBusy) IsGettingIp = false;
                return;
            }
            PublicIpText = "определяется...";
            try
            {
                var ip = await DiagnosticsService.GetPublicIpAsync();
                PublicIpText = ip;
                // Само ЗНАЧЕНИЕ внешнего IP в журнал не пишем — пишем только факт и исход.
                AppLogger.Write(ip == "не определён"
                    ? "[Сеть] Внешний IP не определён"
                    : "[Сеть] Внешний IP определён");
            }
            finally { if (!IsBusy) IsGettingIp = false; }
        }

        // ── DNS ──────────────────────────────────────────────────────────────

        private async Task RunDnsAsync()
        {
            IsCheckingDns = true;
            DnsResultVisible = true;
            // Параноидальный режим: DNS-резолюция через внешний резолвер тоже сетевой
            // запрос вне разрешённых исключений — пропускаем (см. RunPingAsync).
            if (ProfileService.Current.ParanoidMode)
            {
                DnsResultText = "Отключено (параноидальный режим)";
                AppLogger.Write("[Сеть] DNS-проверка пропущена: параноидальный режим");
                if (!IsBusy) IsCheckingDns = false;
                return;
            }
            DnsResultText = "Проверка DNS...";
            try
            {
                var result = await DiagnosticsService.CheckDnsAsync("google.com");
                DnsResultText = result;
                AppLogger.Write("[Сеть] DNS проверка завершена");
            }
            catch (Exception ex) { DnsResultText = $"Ошибка: {ex.Message}"; }
            finally { if (!IsBusy) IsCheckingDns = false; }
        }

        // ── Сброс сети ───────────────────────────────────────────────────────

        private async Task RunResetNetworkAsync()
        {
            var confirm = MessageBox.Show(
                "Сброс сетевых настроек:\n\n" +
                "• netsh winsock reset\n• netsh int ip reset\n• ipconfig /release\n• ipconfig /renew\n\n" +
                "Потребуются права администратора и перезагрузка.\n\nПродолжить?",
                "Сброс сети", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            IsResettingNetwork = true;
            try
            {
                AppLogger.Write("[Сеть] Запуск сброса сетевых настроек...");
                // Приложение уже работает с правами администратора (перезапуск через UAC
                // в MainWindow), поэтому runas не нужен — запускаем скрыто и перенаправляем
                // вывод команд в лог-панель вместо отдельного окна консоли.
                var psi = new ProcessStartInfo
                {
                    FileName  = TrustedExecutablePaths.CmdExe,
                    Arguments = "/c netsh winsock reset & netsh int ip reset & " +
                                "ipconfig /release & ipconfig /renew",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    WindowStyle            = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true
                };
                int exitCode = -1;
                using var p = Process.Start(psi);
                if (p != null)
                {
                    var stdoutTask = p.StandardOutput.ReadToEndAsync();
                    var stderrTask = p.StandardError.ReadToEndAsync();
                    await p.WaitForExitAsync();
                    exitCode = p.ExitCode;

                    foreach (var line in (await stdoutTask).Split('\n'))
                    {
                        var t = line.Trim();
                        if (!string.IsNullOrWhiteSpace(t)) AppLogger.Write($"[Сеть] {t}");
                    }
                    var err = (await stderrTask).Trim();
                    if (!string.IsNullOrWhiteSpace(err)) AppLogger.Write($"[Сеть] ⚠ {err}");
                }

                // Цепочка команд через «&» возвращает код последней, ненулевой код
                // означает, что часть сброса не удалась (нет прав, DHCP и т.п.).
                if (exitCode == 0)
                {
                    AppLogger.Write("[Сеть] Сброс завершён");
                    MessageBox.Show("Перезагрузите компьютер для применения изменений.",
                        "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    AppLogger.Write($"[Сеть] ⚠ Сброс завершился с кодом {exitCode} — часть команд могла не выполниться");
                    MessageBox.Show(
                        $"Сброс сетевых настроек завершился с ошибкой (код {exitCode}). Часть команд могла не выполниться.\n\n" +
                        "Запустите приложение от имени администратора и попробуйте ещё раз. Подробности — в логах.",
                        "Сброс не завершён", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Сеть] Ошибка сброса: {ex.Message}");
                MessageBox.Show("Не удалось сбросить сетевые настройки. Запустите приложение от имени администратора и попробуйте ещё раз.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            // Оригинал сбрасывает IsEnabled безусловно (без "if (!_busy)") — сброс сети
            // не вызывается из RunAllAsync, так что busy-гонки здесь нет по построению.
            finally { IsResettingNetwork = false; }
        }
    }
}
```

- [ ] **Step 2: Написать `tests/Ven4Tools.Tests/NetworkViewModelTests.cs`**

Полное содержимое файла:

```csharp
using System.Windows.Media;
using Ven4Tools.ViewModels;
using Xunit;

namespace Ven4Tools.Tests
{
    public class NetworkViewModelTests
    {
        [Fact]
        public void SetRow_OkNull_УстанавливаетНейтральнуюИконку()
        {
            var row = new NetworkCheckResult();

            NetworkViewModel.SetRow(row, "отключено", null);

            Assert.Equal("отключено", row.Text);
            Assert.Equal("⬜", row.IconText);
            Assert.Same(Brushes.Gray, row.IconBrush);
        }

        [Fact]
        public void SetRow_OkTrue_УстанавливаетЗелёнуюИконку()
        {
            var row = new NetworkCheckResult();

            NetworkViewModel.SetRow(row, "12 мс", true);

            Assert.Equal("12 мс", row.Text);
            Assert.Equal("✅", row.IconText);
            Assert.Equal(Color.FromRgb(74, 222, 128), ((SolidColorBrush)row.IconBrush).Color);
        }

        [Fact]
        public void SetRow_OkFalse_УстанавливаетКраснуюИконку()
        {
            var row = new NetworkCheckResult();

            NetworkViewModel.SetRow(row, "недоступен", false);

            Assert.Equal("недоступен", row.Text);
            Assert.Equal("❌", row.IconText);
            Assert.Equal(Colors.LightCoral, ((SolidColorBrush)row.IconBrush).Color);
        }

        [Fact]
        public void NetworkCheckResult_ДефолтнаяКисть_БезApplication_ПадаетВБелыйФолбэк()
        {
            Assert.Null(System.Windows.Application.Current);

            var row = new NetworkCheckResult();

            Assert.Same(Brushes.White, row.IconBrush);
        }

        [Fact]
        public void NetworkCheckResult_Дефолты_СовпадаютСОригиналомXaml()
        {
            var row = new NetworkCheckResult();

            Assert.Equal("—", row.Text);
            Assert.Equal("⬜", row.IconText);
        }

        [Fact]
        public void Конструктор_УстанавливаетДефолтныеЗначения()
        {
            var vm = new NetworkViewModel();

            Assert.Equal("🔍 Запустить полную диагностику", vm.RunAllButtonText);
            Assert.Equal("не определён", vm.PublicIpText);
            Assert.Equal("", vm.DnsResultText);
            Assert.False(vm.DnsResultVisible);
            Assert.False(vm.AdaptersEmpty);
            Assert.Empty(vm.Adapters);
            Assert.False(vm.IsBusy);
            Assert.False(vm.IsPinging);
            Assert.False(vm.IsCheckingServices);
            Assert.False(vm.IsGettingIp);
            Assert.False(vm.IsCheckingDns);
            Assert.False(vm.IsResettingNetwork);
        }

        [Fact]
        public void ВсеКоманды_ИзначальноCanExecute()
        {
            var vm = new NetworkViewModel();

            Assert.True(vm.RunAllCommand.CanExecute(null));
            Assert.True(vm.RefreshAdaptersCommand.CanExecute(null));
            Assert.True(vm.PingCommand.CanExecute(null));
            Assert.True(vm.CheckServicesCommand.CanExecute(null));
            Assert.True(vm.GetIpCommand.CanExecute(null));
            Assert.True(vm.CheckDnsCommand.CanExecute(null));
            Assert.True(vm.ResetNetworkCommand.CanExecute(null));
        }

        [Fact]
        public void PingRows_СозданыКакНезависимыеЭкземпляры()
        {
            var vm = new NetworkViewModel();

            Assert.NotSame(vm.Ping1, vm.Ping2);
            Assert.NotSame(vm.Ping3, vm.Ping4);
            Assert.NotSame(vm.Svc1, vm.Svc5);
        }
    }
}
```

- [ ] **Step 3: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок, 0 предупреждений.

- [ ] **Step 4: Commit**

```bash
git add Ven4Tools/ViewModels/NetworkViewModel.cs tests/Ven4Tools.Tests/NetworkViewModelTests.cs
git commit -m "feat(network): NetworkViewModel + юнит-тесты"
```

---

### Task 2: Переписать `NetworkTab.xaml`/`NetworkTab.xaml.cs` на тонкую обёртку

**Files:**
- Modify: `Ven4Tools/Views/Tabs/NetworkTab.xaml`
- Modify: `Ven4Tools/Views/Tabs/NetworkTab.xaml.cs`

**Interfaces:**
- Consumes: `Ven4Tools.ViewModels.NetworkViewModel` (Task 1) — все публичные члены.
- Produces: `NetworkTab` без публичного контракта сверх конструктора.

- [ ] **Step 1: Переписать `Ven4Tools/Views/Tabs/NetworkTab.xaml`**

Полное содержимое файла:

```xml
<UserControl x:Class="Ven4Tools.Views.Tabs.NetworkTab"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="{DynamicResource ContentBackground}">
    <UserControl.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis"/>
    </UserControl.Resources>
    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <StackPanel Margin="20">

            <TextBlock Text="🌐 Диагностика сети" FontSize="24" FontWeight="Bold"
                       Foreground="{DynamicResource TextPrimary}" Margin="0,0,0,6"/>
            <TextBlock Text="Проверка соединения, адаптеров, задержки и доступности сервисов"
                       Foreground="{DynamicResource TextSecondary}" TextWrapping="Wrap" Margin="0,0,0,18"/>

            <!-- Главная кнопка -->
            <Button x:Name="btnRunAll" Content="{Binding RunAllButtonText}"
                    ToolTip="Проверит сетевые адаптеры, задержку, доступность сервисов, внешний IP и DNS. Настройки не изменяет. В параноидальном режиме выполняется только чтение адаптеров, остальные проверки пропускаются."
                    Height="42" FontSize="14" FontWeight="SemiBold"
                    HorizontalAlignment="Left" MinWidth="280"
                    Margin="0,0,0,20"
                    Command="{Binding RunAllCommand}"/>

            <!-- Адаптеры -->
            <GroupBox Header="🖥️ Сетевые адаптеры" Margin="0,0,0,14">
                <StackPanel Margin="10">
                    <ItemsControl x:Name="lstAdapters" ItemsSource="{Binding Adapters}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <Border Padding="8,6" Margin="0,2">
                                    <Grid>
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="*"/>
                                            <ColumnDefinition Width="Auto"/>
                                        </Grid.ColumnDefinitions>
                                        <StackPanel>
                                            <TextBlock Text="{Binding Name}" FontWeight="SemiBold"
                                                       Foreground="{DynamicResource TextPrimary}" FontSize="13"/>
                                            <TextBlock FontSize="11" Foreground="{DynamicResource TextSecondary}">
                                                <Run Text="{Binding Type}"/><Run Text="  ·  "/><Run Text="{Binding Ip}"/>
                                            </TextBlock>
                                        </StackPanel>
                                        <TextBlock Grid.Column="1" Text="● Активен"
                                                   Foreground="#4ade80" FontSize="11"
                                                   VerticalAlignment="Center"/>
                                    </Grid>
                                </Border>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                    <TextBlock x:Name="txtAdaptersEmpty" Text="Нет активных адаптеров"
                               Foreground="{DynamicResource TextSecondary}" FontSize="12"
                               Visibility="{Binding AdaptersEmpty, Converter={StaticResource BoolToVis}}"/>
                    <Button x:Name="btnRefreshAdapters" Content="🔄 Обновить"
                            ToolTip="Повторно считает состояние и параметры сетевых адаптеров."
                            Height="30" Width="100" HorizontalAlignment="Left" Margin="0,8,0,0"
                            Command="{Binding RefreshAdaptersCommand}"/>
                </StackPanel>
            </GroupBox>

            <!-- Пинг -->
            <GroupBox Header="📡 Пинг и задержка" Margin="0,0,0,14">
                <StackPanel Margin="10">
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="160"/>
                            <ColumnDefinition Width="80"/>
                            <ColumnDefinition Width="*"/>
                        </Grid.ColumnDefinitions>
                        <Grid.RowDefinitions>
                            <RowDefinition Height="28"/>
                            <RowDefinition Height="28"/>
                            <RowDefinition Height="28"/>
                            <RowDefinition Height="28"/>
                        </Grid.RowDefinitions>

                        <TextBlock Grid.Row="0" Grid.Column="0" Text="1.1.1.1 (Cloudflare)" VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}" FontSize="12"/>
                        <TextBlock Grid.Row="1" Grid.Column="0" Text="8.8.8.8 (Google DNS)" VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}" FontSize="12"/>
                        <TextBlock Grid.Row="2" Grid.Column="0" Text="google.com" VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}" FontSize="12"/>
                        <TextBlock Grid.Row="3" Grid.Column="0" Text="ven4tools.ru" VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}" FontSize="12"/>

                        <TextBlock x:Name="txtPing1" Grid.Row="0" Grid.Column="1" Text="{Binding Ping1.Text}" VerticalAlignment="Center" Foreground="{DynamicResource TextPrimary}" FontFamily="JetBrains Mono, Consolas" FontSize="12"/>
                        <TextBlock x:Name="txtPing2" Grid.Row="1" Grid.Column="1" Text="{Binding Ping2.Text}" VerticalAlignment="Center" Foreground="{DynamicResource TextPrimary}" FontFamily="JetBrains Mono, Consolas" FontSize="12"/>
                        <TextBlock x:Name="txtPing3" Grid.Row="2" Grid.Column="1" Text="{Binding Ping3.Text}" VerticalAlignment="Center" Foreground="{DynamicResource TextPrimary}" FontFamily="JetBrains Mono, Consolas" FontSize="12"/>
                        <TextBlock x:Name="txtPing4" Grid.Row="3" Grid.Column="1" Text="{Binding Ping4.Text}" VerticalAlignment="Center" Foreground="{DynamicResource TextPrimary}" FontFamily="JetBrains Mono, Consolas" FontSize="12"/>

                        <TextBlock x:Name="txtPingIcon1" Grid.Row="0" Grid.Column="2" Text="{Binding Ping1.IconText}" Foreground="{Binding Ping1.IconBrush}" VerticalAlignment="Center" Margin="6,0,0,0" FontSize="13"/>
                        <TextBlock x:Name="txtPingIcon2" Grid.Row="1" Grid.Column="2" Text="{Binding Ping2.IconText}" Foreground="{Binding Ping2.IconBrush}" VerticalAlignment="Center" Margin="6,0,0,0" FontSize="13"/>
                        <TextBlock x:Name="txtPingIcon3" Grid.Row="2" Grid.Column="2" Text="{Binding Ping3.IconText}" Foreground="{Binding Ping3.IconBrush}" VerticalAlignment="Center" Margin="6,0,0,0" FontSize="13"/>
                        <TextBlock x:Name="txtPingIcon4" Grid.Row="3" Grid.Column="2" Text="{Binding Ping4.IconText}" Foreground="{Binding Ping4.IconBrush}" VerticalAlignment="Center" Margin="6,0,0,0" FontSize="13"/>
                    </Grid>
                    <Button x:Name="btnPing" Content="📡 Пинговать" Height="30" Width="120"
                            ToolTip="Измерит задержку и потери пакетов до указанных адресов. В параноидальном режиме запросы не отправляются."
                            HorizontalAlignment="Left" Margin="0,10,0,0"
                            Command="{Binding PingCommand}"/>
                </StackPanel>
            </GroupBox>

            <!-- Доступность сервисов -->
            <GroupBox Header="🌍 Доступность сервисов" Margin="0,0,0,14">
                <StackPanel Margin="10">
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="140"/>
                            <ColumnDefinition Width="60"/>
                            <ColumnDefinition Width="*"/>
                        </Grid.ColumnDefinitions>
                        <Grid.RowDefinitions>
                            <RowDefinition Height="28"/>
                            <RowDefinition Height="28"/>
                            <RowDefinition Height="28"/>
                            <RowDefinition Height="28"/>
                            <RowDefinition Height="28"/>
                        </Grid.RowDefinitions>

                        <TextBlock Grid.Row="0" Grid.Column="0" Text="Google" VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}" FontSize="12"/>
                        <TextBlock Grid.Row="1" Grid.Column="0" Text="YouTube" VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}" FontSize="12"/>
                        <TextBlock Grid.Row="2" Grid.Column="0" Text="Discord" VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}" FontSize="12"/>
                        <TextBlock Grid.Row="3" Grid.Column="0" Text="Cloudflare" VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}" FontSize="12"/>
                        <TextBlock Grid.Row="4" Grid.Column="0" Text="GitHub" VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}" FontSize="12"/>

                        <TextBlock x:Name="txtSvc1" Grid.Row="0" Grid.Column="1" Text="{Binding Svc1.IconText}" Foreground="{Binding Svc1.IconBrush}" VerticalAlignment="Center" FontSize="14"/>
                        <TextBlock x:Name="txtSvc2" Grid.Row="1" Grid.Column="1" Text="{Binding Svc2.IconText}" Foreground="{Binding Svc2.IconBrush}" VerticalAlignment="Center" FontSize="14"/>
                        <TextBlock x:Name="txtSvc3" Grid.Row="2" Grid.Column="1" Text="{Binding Svc3.IconText}" Foreground="{Binding Svc3.IconBrush}" VerticalAlignment="Center" FontSize="14"/>
                        <TextBlock x:Name="txtSvc4" Grid.Row="3" Grid.Column="1" Text="{Binding Svc4.IconText}" Foreground="{Binding Svc4.IconBrush}" VerticalAlignment="Center" FontSize="14"/>
                        <TextBlock x:Name="txtSvc5" Grid.Row="4" Grid.Column="1" Text="{Binding Svc5.IconText}" Foreground="{Binding Svc5.IconBrush}" VerticalAlignment="Center" FontSize="14"/>

                        <TextBlock x:Name="txtSvcMs1" Grid.Row="0" Grid.Column="2" Text="{Binding Svc1.Text}" VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}" FontSize="11" Margin="6,0,0,0"/>
                        <TextBlock x:Name="txtSvcMs2" Grid.Row="1" Grid.Column="2" Text="{Binding Svc2.Text}" VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}" FontSize="11" Margin="6,0,0,0"/>
                        <TextBlock x:Name="txtSvcMs3" Grid.Row="2" Grid.Column="2" Text="{Binding Svc3.Text}" VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}" FontSize="11" Margin="6,0,0,0"/>
                        <TextBlock x:Name="txtSvcMs4" Grid.Row="3" Grid.Column="2" Text="{Binding Svc4.Text}" VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}" FontSize="11" Margin="6,0,0,0"/>
                        <TextBlock x:Name="txtSvcMs5" Grid.Row="4" Grid.Column="2" Text="{Binding Svc5.Text}" VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}" FontSize="11" Margin="6,0,0,0"/>
                    </Grid>
                    <Button x:Name="btnCheckServices" Content="🌍 Проверить сервисы" Height="30" Width="180"
                            ToolTip="Проверит, открываются ли основные интернет-сервисы по HTTPS. В параноидальном режиме запросы не отправляются."
                            HorizontalAlignment="Left" Margin="0,10,0,0"
                            Command="{Binding CheckServicesCommand}"/>
                </StackPanel>
            </GroupBox>

            <!-- Внешний IP -->
            <GroupBox Header="🌐 Внешний IP" Margin="0,0,0,14">
                <StackPanel Margin="10" Orientation="Horizontal">
                    <TextBlock Text="IP: " Foreground="{DynamicResource TextSecondary}" VerticalAlignment="Center"/>
                    <TextBlock x:Name="txtPublicIp" Text="{Binding PublicIpText}"
                               Foreground="{DynamicResource TextPrimary}" FontFamily="JetBrains Mono, Consolas"
                               FontSize="13" VerticalAlignment="Center" Margin="4,0,14,0"/>
                    <Button x:Name="btnGetIp" Content="Определить" Height="30" Width="110"
                            ToolTip="Запросит у внешнего сервиса ваш текущий публичный IP-адрес. В параноидальном режиме запрос не выполняется."
                            Command="{Binding GetIpCommand}"/>
                </StackPanel>
            </GroupBox>

            <!-- DNS -->
            <GroupBox Header="🔍 DNS" Margin="0,0,0,14">
                <StackPanel Margin="10">
                    <Button x:Name="btnCheckDns" Content="🔍 Проверить DNS (google.com)" Height="35"
                            ToolTip="Проверит, может ли Windows преобразовать имя google.com в IP-адрес. В параноидальном режиме проверка не выполняется."
                            Width="260" HorizontalAlignment="Left"
                            Command="{Binding CheckDnsCommand}"/>
                    <TextBlock x:Name="txtDnsResult" Text="{Binding DnsResultText}" Foreground="{DynamicResource TextSecondary}"
                               FontSize="11" FontFamily="JetBrains Mono, Consolas"
                               TextWrapping="Wrap" Margin="0,8,0,0"
                               Visibility="{Binding DnsResultVisible, Converter={StaticResource BoolToVis}}"/>
                </StackPanel>
            </GroupBox>

            <!-- Инструменты -->
            <GroupBox Header="⚙️ Инструменты" Margin="0,0,0,14">
                <StackPanel Margin="10">
                    <Button x:Name="btnResetNetwork" Content="🔄 Сбросить сетевые настройки (winsock, IP)"
                            ToolTip="После подтверждения сбросит Winsock и параметры IP. Сеть временно отключится, может потребоваться перезагрузка."
                            Height="35" Width="320" HorizontalAlignment="Left"
                            Command="{Binding ResetNetworkCommand}"/>
                    <TextBlock Text="Потребуются права администратора и перезагрузка"
                               Foreground="{DynamicResource TextSecondary}" FontSize="11" Margin="0,6,0,0"/>
                </StackPanel>
            </GroupBox>

        </StackPanel>
    </ScrollViewer>
</UserControl>
```

- [ ] **Step 2: Переписать `Ven4Tools/Views/Tabs/NetworkTab.xaml.cs`**

Полное содержимое файла:

```csharp
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «Сеть» — тонкая обёртка над <see cref="NetworkViewModel"/>.
    /// Вся логика перенесена в ViewModel при MVVM-миграции (2026-08-25, пятая
    /// вкладка после DebloaterTab/HistoryTab/AboutTab/ActivationTab). Публичного
    /// контракта сверх конструктора нет.
    /// </summary>
    public partial class NetworkTab : System.Windows.Controls.UserControl
    {
        private readonly NetworkViewModel _viewModel = new();

        public NetworkTab()
        {
            InitializeComponent();
            DataContext = _viewModel;

            Loaded += (_, _) => _viewModel.RefreshAdaptersCommand.Execute(null);
        }
    }
}
```

- [ ] **Step 3: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок, 0 предупреждений — во всех проектах, включая `Ven4Tools.ClientUITests`.

- [ ] **Step 4: Commit**

```bash
git add Ven4Tools/Views/Tabs/NetworkTab.xaml Ven4Tools/Views/Tabs/NetworkTab.xaml.cs
git commit -m "refactor(network): NetworkTab — тонкая обёртка над NetworkViewModel"
```

---

### Task 3: Верификация — регрессия существующих тестов

**Files:**
- Не создаёт и не меняет файлы.

**Interfaces:**
- Не применимо.

- [ ] **Step 1: Полная сборка Release**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0/0.

- [ ] **Step 2: Юнит-тесты целиком на VenchWork**

Run (на VenchWork): `dotnet test tests/Ven4Tools.Tests -c Release`
Expected: было 422/422 после ActivationTab (см. память `project_ven4tools_mvvm_migration_activationtab_2026_08_25`) + новые из `NetworkViewModelTests` (8 тестов по Step 2 Task 1) = 430/430.

- [ ] **Step 3: Существующие UI-тесты на VenchWork**

Run (на VenchWork): `dotnet test Ven4Tools.ClientUITests -c Release --filter "FullyQualifiedName~Phase3RemainingTabsTests|FullyQualifiedName~KeyButtonsSmokeTests"`
Expected: `NetworkTab_ОстальныеДиагностическиеКнопки` и все остальные тесты обоих классов — зелёные, не хуже прежнего результата (13/13 после ActivationTab).

- [ ] **Step 4: Финальный коммит верификации**

```bash
git add -A
git status
git commit -m "test(network): MVVM-миграция NetworkTab проверена на VenchWork" --allow-empty
```

- [ ] **Step 5: Финальное цельное ревью ветки**

Обязательный шаг перед мерджем (см. Global Constraints и критерий готовности спеки) — точечные ревью Task 1/Task 2 структурно не видят межзадачные пробелы; в предыдущих 4 вкладках этот шаг трижды подряд находил реальные находки. Пакет для ревью: `scripts/review-package <merge-base main mvvm-networktab> HEAD`.

- [ ] **Step 6: Merge + push в `main`** (без дополнительного вопроса — автономная сессия)

```bash
git checkout main
git merge --ff-only mvvm-networktab
dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental
git push origin main
git branch -d mvvm-networktab
```

Перед пушем — обязательно проверить все коммиты ветки на `Claude-Session`-трейлер: `git log main..mvvm-networktab --format="%B" | grep -i claude` (должно быть пусто).

---

## После задачи

Смержено и запушено в `main`. Следующая по сложности вкладка — `OfficeTab` (686 строк) — тот же процесс, новая ветка от `main`.
