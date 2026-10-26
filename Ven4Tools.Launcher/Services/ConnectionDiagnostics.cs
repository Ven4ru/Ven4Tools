using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Launcher.Helpers;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>Какой источник проверяется.</summary>
    internal enum DiagSource
    {
        CdnDomain,
        CdnDirectIp,
        Mirror,
        GitHub
    }

    /// <summary>Результат проверки одного источника. null в полях — шаг не выполнялся.</summary>
    internal sealed class SourceProbe
    {
        public DiagSource Source { get; init; }
        public string Name { get; init; } = "";
        public string Host { get; init; } = "";

        public bool? DnsOk { get; set; }
        public string? DnsAddresses { get; set; }
        public string? DnsError { get; set; }

        public bool? TcpOk { get; set; }
        public long? TcpMs { get; set; }
        public string? TcpError { get; set; }

        public bool? TlsOk { get; set; }
        public string? TlsError { get; set; }
        public bool TlsCertificateProblem { get; set; }
        public string? TlsIssuer { get; set; }

        public bool Proxied { get; set; }
        public int? HttpStatus { get; set; }
        public string? HttpError { get; set; }
        public bool Proxy407 { get; set; }
        public long BytesRead { get; set; }
        public double Seconds { get; set; }
        public bool Stalled { get; set; }
        /// <summary>Замер скорости выполнялся по большому файлу (а не по короткой странице).</summary>
        public bool SpeedMeasured { get; set; }

        public DateTimeOffset? ServerDate { get; set; }
        public DateTimeOffset? LocalAtResponse { get; set; }

        /// <summary>Источник рабочий: ответ HTTP получен, и данные шли без обрыва.</summary>
        public bool HttpOk => HttpStatus is >= 200 and < 400 && !Stalled && !Proxy407;

        /// <summary>Похоже на ограничение «первые ~16 КБ»: данные пошли и замерли почти сразу.</summary>
        public bool LooksThrottled => Stalled && BytesRead > 0 && BytesRead <= ConnectionDiagnostics.ThrottleSuspectBytes;

        public double? SpeedKBps => SpeedMeasured && Seconds > 0 && BytesRead > 0 && !Stalled ? BytesRead / 1024.0 / Seconds : null;
    }

    /// <summary>Итог диагностики.</summary>
    internal sealed class DiagnosticsResult
    {
        public List<SourceProbe> Probes { get; } = new();
        public TimeSpan? ClockSkew { get; set; }
        public string ProxyDescription { get; set; } = "";
        public List<string> Conclusions { get; set; } = new();
        public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Л8 «Диагностика соединения» одной кнопкой. Только чтение: по каждому источнику
    /// загрузки (CDN по домену, CDN по прямому IP, зеркало ven4tools.ru, GitHub) — DNS,
    /// TCP, TLS, HTTP с замером скорости (Range-запрос ~1 МБ к реальному файлу релиза),
    /// прокси, сверка часов ПК с заголовком Date. Итог — понятные выводы по-русски
    /// (<see cref="Summarize"/>, чистая функция) и отчёт для буфера обмена без имени ПК,
    /// пользователя и путей (<see cref="BuildReport"/>). Никаких сторонних сайтов
    /// «для проверки интернета» — только наши источники.
    /// </summary>
    internal static class ConnectionDiagnostics
    {
        internal const string CdnHost = "cdn.ven4tools.ru";
        internal const string MirrorHost = "ven4tools.ru";
        internal const string GitHubHost = "github.com";

        // Порог «оборвалось почти сразу»: ограничение провайдеров — первые ~16 КБ, с
        // запасом на заголовки и TLS-записи считаем подозрительным всё до 64 КБ.
        internal const long ThrottleSuspectBytes = 64 * 1024;
        private const long SpeedTestBytes = 1024 * 1024;

        private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(7);
        private static readonly TimeSpan ReadStallTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan SpeedTestBudget = TimeSpan.FromSeconds(20);

        // Без общего таймаута клиента: каждый шаг ограничивает себя сам.
        private static readonly HttpClient _http = LauncherHttp.CreateClient(Timeout.InfiniteTimeSpan);

        public static async Task<DiagnosticsResult> RunAsync(IProgress<string>? progress, CancellationToken ct)
        {
            var result = new DiagnosticsResult { ProxyDescription = DescribeProxy() };

            // Адреса реальных файлов релиза для замера скорости — из подписанного манифеста.
            progress?.Report("Получаю список источников…");
            CdnVersionInfo? manifest = null;
            try
            {
                using var cdn = new CdnService(m => LauncherLog.Write($"[Диагностика] CDN: {m}"));
                manifest = await cdn.GetVersionInfoAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LauncherLog.Write($"[Диагностика] Манифест не получен: {ex.Message}");
            }

            string cdnIp = CdnService.LastKnownCdnIp ?? IpPinnedHttpClientFactory.FallbackCdnIp;
            var c = manifest?.Client;
            var plan = new (DiagSource Source, string Name, string Host, string? BigUrl, string SmallUrl)[]
            {
                (DiagSource.CdnDomain, "CDN (cdn.ven4tools.ru)", CdnHost, c?.ZipUrl, "https://cdn.ven4tools.ru/version.json"),
                (DiagSource.CdnDirectIp, $"CDN по прямому IP ({cdnIp})", CdnHost, c?.ZipUrl, "https://cdn.ven4tools.ru/version.json"),
                (DiagSource.Mirror, "Зеркало ven4tools.ru", MirrorHost, c?.ZipMirrorHosting, "https://ven4tools.ru/"),
                (DiagSource.GitHub, "GitHub", GitHubHost, c?.ZipFallback, "https://github.com/Ven4ru/Ven4Tools/releases"),
            };

            foreach (var p in plan)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Проверяю: {p.Name}…");
                var probe = new SourceProbe { Source = p.Source, Name = p.Name, Host = p.Host };
                await ProbeAsync(probe, cdnIp, p.BigUrl, p.SmallUrl, ct).ConfigureAwait(false);
                result.Probes.Add(probe);
            }

            result.ClockSkew = EstimateClockSkew(result.Probes);
            result.Conclusions = Summarize(result.Probes, result.ClockSkew);
            return result;
        }

        private static async Task ProbeAsync(SourceProbe probe, string cdnIp, string? bigUrl, string smallUrl, CancellationToken ct)
        {
            bool directIp = probe.Source == DiagSource.CdnDirectIp;
            string url = !string.IsNullOrEmpty(bigUrl) && DownloadValidator.IsAllowedDownloadHost(bigUrl) ? bigUrl : smallUrl;
            probe.Proxied = !directIp && LauncherProxy.IsProxied(new Uri(url));

            // DNS (у «прямого IP» — пропускаем: в этом и смысл источника).
            IPAddress[] addresses = Array.Empty<IPAddress>();
            if (directIp)
            {
                if (IPAddress.TryParse(cdnIp, out var ip)) addresses = new[] { ip };
            }
            else
            {
                try
                {
                    using var cts = Linked(ct, StepTimeout);
                    addresses = await Dns.GetHostAddressesAsync(probe.Host, cts.Token).ConfigureAwait(false);
                    probe.DnsOk = addresses.Length > 0;
                    probe.DnsAddresses = string.Join(", ", addresses.Take(4).Select(a => a.ToString()));
                    if (addresses.Length == 0) probe.DnsError = "имя не разрешилось ни в один адрес";
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    probe.DnsOk = false;
                    probe.DnsError = Short(ex);
                }
            }

            // TCP + TLS напрямую (мимо прокси — показывают, что делает с трафиком провайдер).
            var target = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
            if (target != null)
                await ProbeTcpTlsAsync(probe, target, ct).ConfigureAwait(false);

            // HTTP: через прокси лаунчера (как настоящая загрузка); «прямой IP» — напрямую.
            HttpClient client = directIp ? IpPinnedHttpClientFactory.GetOrCreate(cdnIp, Timeout.InfiniteTimeSpan) : _http;
            await ProbeHttpAsync(probe, client, url, url == bigUrl, ct).ConfigureAwait(false);
        }

        private static async Task ProbeTcpTlsAsync(SourceProbe probe, IPAddress address, CancellationToken ct)
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = Linked(ct, StepTimeout);
                await socket.ConnectAsync(address, 443, cts.Token).ConfigureAwait(false);
                probe.TcpOk = true;
                probe.TcpMs = sw.ElapsedMilliseconds;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                probe.TcpOk = false;
                probe.TcpError = ex is OperationCanceledException ? "нет ответа за 7 с" : Short(ex);
                return;
            }

            SslPolicyErrors seen = SslPolicyErrors.None;
            string? issuer = null;
            try
            {
                using var cts = Linked(ct, StepTimeout);
                await using var stream = new NetworkStream(socket, ownsSocket: false);
                await using var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                var options = new SslClientAuthenticationOptions
                {
                    TargetHost = probe.Host,
                    // Проверка штатная: колбэк только ЗАПОМИНАЕТ, что не понравилось, и
                    // возвращает тот же вердикт, что вынесла бы система. Ничего не разрешает.
                    RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                    {
                        seen = errors;
                        issuer = cert?.Issuer;
                        return errors == SslPolicyErrors.None;
                    },
                };
                await ssl.AuthenticateAsClientAsync(options, cts.Token).ConfigureAwait(false);
                probe.TlsOk = true;
                probe.TlsIssuer = ShortIssuer(issuer);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                probe.TlsOk = false;
                probe.TlsError = ex is OperationCanceledException ? "нет ответа за 7 с" : Short(ex);
                probe.TlsCertificateProblem = seen != SslPolicyErrors.None;
                if (probe.TlsCertificateProblem)
                {
                    probe.TlsIssuer = ShortIssuer(issuer);
                    probe.TlsError = $"сертификат не принят ({seen})" + (probe.TlsIssuer != null ? $", {probe.TlsIssuer}" : "");
                }
            }
        }

        private static async Task ProbeHttpAsync(SourceProbe probe, HttpClient client, string url, bool speedTest, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (speedTest) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, SpeedTestBytes - 1);
            try
            {
                using var headersCts = Linked(ct, TimeSpan.FromSeconds(15));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersCts.Token).ConfigureAwait(false);
                probe.LocalAtResponse = DateTimeOffset.UtcNow;
                probe.ServerDate = response.Headers.Date;
                probe.HttpStatus = (int)response.StatusCode;
                probe.Proxy407 = response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired;
                if (!response.IsSuccessStatusCode) return;
                if (speedTest && !DownloadValidator.IsAllowedDownloadHostAfterRedirect(response))
                {
                    probe.HttpError = "переадресация на недоверенный адрес — замер прерван";
                    return;
                }

                probe.SpeedMeasured = speedTest;
                await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var buffer = new byte[64 * 1024];
                var bodyWatch = Stopwatch.StartNew();
                long limit = speedTest ? SpeedTestBytes : 256 * 1024;
                while (probe.BytesRead < limit)
                {
                    if (bodyWatch.Elapsed > SpeedTestBudget) break; // медленно, но живо — не «обрыв»
                    using var readCts = Linked(ct, ReadStallTimeout);
                    int n;
                    try
                    {
                        n = await body.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        probe.Stalled = true;
                        break;
                    }
                    if (n == 0) break;
                    probe.BytesRead += n;
                }
                probe.Seconds = bodyWatch.Elapsed.TotalSeconds;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                probe.HttpError = ex is OperationCanceledException ? "нет ответа за 15 с" : Short(ex);
                if (ex is HttpRequestException { HttpRequestError: HttpRequestError.ProxyTunnelError } && ex.Message.Contains("407", StringComparison.Ordinal))
                    probe.Proxy407 = true;
            }
            finally
            {
                if (probe.Seconds == 0) probe.Seconds = sw.Elapsed.TotalSeconds;
            }
        }

        /// <summary>
        /// Расхождение часов: медиана (Date сервера − время ПК в момент ответа) по всем
        /// ответившим источникам. Положительное — часы ПК отстают. Чистая функция.
        /// </summary>
        internal static TimeSpan? EstimateClockSkew(IEnumerable<SourceProbe> probes)
        {
            var diffs = probes
                .Where(p => p.ServerDate != null && p.LocalAtResponse != null)
                .Select(p => (p.ServerDate!.Value - p.LocalAtResponse!.Value).Ticks)
                .OrderBy(t => t)
                .ToList();
            if (diffs.Count == 0) return null;
            return TimeSpan.FromTicks(diffs[diffs.Count / 2]);
        }

        /// <summary>Описание расхождения часов или null, если оно в пределах нормы. Чистая функция.</summary>
        internal static string? DescribeClockSkew(TimeSpan? skew)
        {
            if (skew is not { } s) return null;
            var abs = s.Duration();
            if (abs < TimeSpan.FromMinutes(5)) return null;
            string dir = s > TimeSpan.Zero ? "отстают" : "спешат";
            string amount = abs.TotalDays >= 1
                ? $"{(int)Math.Round(abs.TotalDays)} дн."
                : abs.TotalHours >= 1 ? $"{(int)Math.Round(abs.TotalHours)} ч." : $"{(int)Math.Round(abs.TotalMinutes)} мин.";
            return $"Часы компьютера {dir} на {amount} Из-за неверного времени не проходят защищённые соединения (сертификаты кажутся просроченными или ещё не действующими). " +
                   "Включите «Установить время автоматически» в параметрах Windows.";
        }

        /// <summary>
        /// Понятный итог по результатам проверок. Чистая функция — покрыта unit-тестами.
        /// Порядок выводов — от самого вероятного виновника к следствиям.
        /// </summary>
        internal static List<string> Summarize(IReadOnlyList<SourceProbe> probes, TimeSpan? clockSkew)
        {
            var list = new List<string>();
            SourceProbe? Get(DiagSource s) => probes.FirstOrDefault(p => p.Source == s);
            bool Ok(DiagSource s) => Get(s)?.HttpOk == true;

            if (probes.Any(p => p.Proxy407))
                list.Add("Нужна авторизация прокси (ответ 407): укажите логин и пароль в «Настройки → Прокси» (режим «Вручную»).");

            string? clock = DescribeClockSkew(clockSkew);
            if (clock != null) list.Add(clock);

            if (probes.Count == 0) { list.Add("Нет данных: проверки не выполнялись."); return list; }

            bool cdnOk = Ok(DiagSource.CdnDomain) || Ok(DiagSource.CdnDirectIp);
            bool mirrorOk = Ok(DiagSource.Mirror);
            bool githubOk = Ok(DiagSource.GitHub);

            if (probes.All(p => p.HttpOk))
            {
                var fastest = probes.Where(p => p.SpeedKBps != null).OrderByDescending(p => p.SpeedKBps).FirstOrDefault();
                list.Add("Все источники загрузки работают." +
                         (fastest != null ? $" Быстрее всего — {fastest.Name} ({FormatSpeed(fastest.SpeedKBps!.Value)})." : ""));
            }
            else if (!probes.Any(p => p.HttpOk))
            {
                bool dnsAllFailed = probes.Where(p => p.DnsOk != null).All(p => p.DnsOk == false) && probes.Any(p => p.DnsOk != null);
                if (dnsAllFailed && probes.All(p => p.TcpOk != true))
                    list.Add("Ни один адрес не находится по имени и никуда нет соединения — похоже, интернета нет или не работает DNS. Проверьте подключение.");
                else
                    list.Add("Ни один источник загрузки не отвечает. Проверьте подключение к интернету.");
                list.Add("На мобильном интернете при ограничениях работают только сайты из «белого списка» — подключитесь по Wi-Fi.");
            }
            else
            {
                var domain = Get(DiagSource.CdnDomain);
                if (domain is { HttpOk: false, DnsOk: false } && Ok(DiagSource.CdnDirectIp))
                    list.Add("DNS не находит cdn.ven4tools.ru — лаунчер подключается к нему по IP-адресу, загрузка работает.");
                else if (!cdnOk && mirrorOk)
                    list.Add("Провайдер режет CDN, работает зеркало ven4tools.ru — лаунчер переключится на него сам.");
                else if (!cdnOk && !mirrorOk && githubOk)
                    list.Add("CDN и зеркало ven4tools.ru недоступны, работает только GitHub — загрузка пойдёт с него (может быть медленнее).");

                if (!githubOk && (cdnOk || mirrorOk))
                    list.Add("GitHub недоступен или замедлен — это не мешает: лаунчер качает с CDN и зеркала.");
            }

            foreach (var p in probes.Where(p => p.LooksThrottled))
                list.Add($"{p.Name}: соединение обрывается после первых ~{Math.Max(1, p.BytesRead / 1024)} КБ — так выглядит ограничение провайдера " +
                         "(характерно для ресурсов за Cloudflare и зарубежных хостингов).");

            if (clock == null)
            {
                foreach (var p in probes.Where(p => p.TlsCertificateProblem))
                    list.Add($"{p.Name}: защищённое соединение не проходит проверку сертификата — так бывает, когда трафик подменяет антивирус с проверкой HTTPS " +
                             "или фильтр провайдера/организации.");
            }

            var proxied = probes.Where(p => p.Proxied).ToList();
            if (proxied.Count > 0 && proxied.All(p => !p.HttpOk) && probes.Any(p => !p.Proxied && p.HttpOk))
                list.Add("Через прокси ни один источник не открылся, а напрямую — открылся: прокси, похоже, неисправен. Проверьте «Настройки → Прокси».");

            return list;
        }

        internal static string FormatSpeed(double kbps) =>
            kbps >= 1024 ? $"{(kbps / 1024).ToString("0.0", CultureInfo.InvariantCulture)} МБ/с" : $"{Math.Round(kbps)} КБ/с";

        /// <summary>
        /// Отчёт для буфера обмена: без имени ПК, пользователя и путей (как в окне отчёта
        /// о сбое), без адреса и логина ручного прокси. Чистая функция — покрыта unit-тестами.
        /// </summary>
        internal static string BuildReport(DiagnosticsResult r, string launcherVersion, string osVersion)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Ven4Tools Launcher — диагностика соединения");
            sb.AppendLine($"Время (UTC): {r.StartedUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
            sb.AppendLine($"Лаунчер: {launcherVersion}; Windows: {osVersion}");
            sb.AppendLine($"Прокси: {r.ProxyDescription}");
            sb.AppendLine($"Часы ПК: {(r.ClockSkew is { } s ? (s.Duration() < TimeSpan.FromMinutes(5) ? "в норме" : $"расхождение {FormatSkew(s)}") : "нет данных")}");
            sb.AppendLine();
            foreach (var p in r.Probes)
            {
                sb.AppendLine($"[{p.Name}]");
                if (p.Source != DiagSource.CdnDirectIp)
                    sb.AppendLine($"  DNS: {Status(p.DnsOk, p.DnsOk == true ? p.DnsAddresses : p.DnsError)}");
                sb.AppendLine($"  TCP 443: {Status(p.TcpOk, p.TcpOk == true ? $"{p.TcpMs} мс" : p.TcpError)}");
                sb.AppendLine($"  TLS: {Status(p.TlsOk, p.TlsOk == true ? p.TlsIssuer : p.TlsError)}");
                string http = p.HttpStatus != null ? $"HTTP {p.HttpStatus}" : $"ошибка: {p.HttpError ?? "нет данных"}";
                if (p.Proxied) http += " (через прокси)";
                sb.AppendLine($"  Запрос: {http}");
                if (p.BytesRead > 0 || p.Stalled)
                {
                    string speed = p.SpeedKBps is { } k ? FormatSpeed(k) : p.SpeedMeasured ? "нет данных" : "не замерялась (нет адреса файла)";
                    string seconds = p.Seconds.ToString("0.0", CultureInfo.InvariantCulture);
                    sb.AppendLine(p.Stalled
                        ? $"  Получено: {p.BytesRead / 1024} КБ за {seconds} с; скорость: {speed}; ПОТОК ЗАМЕР"
                        : $"  Получено: {p.BytesRead / 1024} КБ за {seconds} с; скорость: {speed}");
                }
            }
            sb.AppendLine();
            sb.AppendLine("Итог:");
            foreach (var c in r.Conclusions) sb.AppendLine($"  • {c}");
            return PersonalDataSanitizer.Sanitize(sb.ToString().TrimEnd());
        }

        private static string Status(bool? ok, string? detail) => ok switch
        {
            true => "OK" + (string.IsNullOrEmpty(detail) ? "" : $" ({detail})"),
            false => "ОТКАЗ" + (string.IsNullOrEmpty(detail) ? "" : $" ({detail})"),
            null => "не проверялось",
        };

        // Каждый вариант — целый шаблон: склейку из кусков языковой пакет не переводит.
        private static string FormatSkew(TimeSpan s)
        {
            bool behind = s > TimeSpan.Zero;
            var d = s.Duration();
            if (d.TotalDays >= 1)
            {
                int days = (int)d.TotalDays;
                return behind ? $"часы ПК отстают на {days} дн." : $"часы ПК спешат на {days} дн.";
            }
            int minutes = (int)d.TotalMinutes;
            return behind ? $"часы ПК отстают на {minutes} мин." : $"часы ПК спешат на {minutes} мин.";
        }

        /// <summary>Описание прокси без адреса и логина (они могут быть внутренними данными организации).</summary>
        private static string DescribeProxy()
        {
            var mode = LauncherProxy.EffectiveMode;
            bool systemProxy;
            try { systemProxy = LauncherProxy.IsProxied(new Uri("https://cdn.ven4tools.ru/")); }
            catch { systemProxy = false; }
            if (mode == ProxyMode.Manual)
                return "вручную (адрес задан в лаунчере" + (string.IsNullOrEmpty(LauncherProxy.ManualUserName) ? ", без логина)" : ", с логином)");
            return systemProxy ? "системный (в Windows задан прокси)" : "системный (прокси не задан — напрямую)";
        }

        private static CancellationTokenSource Linked(CancellationToken ct, TimeSpan timeout)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            return cts;
        }

        private static string Short(Exception ex)
        {
            Exception inner = ex;
            while (inner.InnerException != null && inner is HttpRequestException or IOException or AuthenticationException)
                inner = inner.InnerException;
            string msg = inner is SocketException se ? $"{se.SocketErrorCode}" : inner.Message;
            return msg.Length > 160 ? msg[..160] + "…" : msg;
        }

        private static string? ShortIssuer(string? issuer)
        {
            if (string.IsNullOrEmpty(issuer)) return null;
            foreach (var part in issuer.Split(','))
            {
                string t = part.Trim();
                if (t.StartsWith("O=", StringComparison.OrdinalIgnoreCase) || t.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                    return "сертификат от " + t[(t.IndexOf('=') + 1)..];
            }
            return issuer;
        }
    }
}
