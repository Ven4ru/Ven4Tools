using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class NetworkViewModel
    {
        // ── Пинг ─────────────────────────────────────────────────────────────

        public NetworkCheckResult Ping1 { get; } = new();
        public NetworkCheckResult Ping2 { get; } = new();
        public NetworkCheckResult Ping3 { get; } = new();
        public NetworkCheckResult Ping4 { get; } = new();

        // ── Доступность сервисов ─────────────────────────────────────────────

        // Оригинальный XAML давал строкам сервисов пустой Text (txtSvcMs1..5 Text=""),
        // в отличие от строк пинга с "—" — сохраняем это различие.
        public NetworkCheckResult Svc1 { get; } = new(initialText: "");
        public NetworkCheckResult Svc2 { get; } = new(initialText: "");
        public NetworkCheckResult Svc3 { get; } = new(initialText: "");
        public NetworkCheckResult Svc4 { get; } = new(initialText: "");
        public NetworkCheckResult Svc5 { get; } = new(initialText: "");

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

        // ── Пинг ─────────────────────────────────────────────────────────────

        private async Task RunPingAsync()
        {
            // Гейт реентерабельности — см. пояснение в RunAllAsync. Проверяется СВОЙ флаг,
            // а не IsBusy: при вызове из полной диагностики IsPinging здесь ещё false,
            // так что штатный сценарий RunAll не обрывается.
            if (IsPinging) return;
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
            // Гейт реентерабельности — см. пояснение в RunAllAsync.
            if (IsCheckingServices) return;
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
            // Гейт реентерабельности — см. пояснение в RunAllAsync.
            if (IsGettingIp) return;
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
            // Гейт реентерабельности — см. пояснение в RunAllAsync.
            if (IsCheckingDns) return;
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
    }
}
