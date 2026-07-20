// Сервис получения информации о версиях и ссылок на загрузку с CDN.
// CDN — основной источник, GitHub — резервный.
using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services
{
    public class CdnService : IDisposable
    {
        private const string VersionUrl = "https://cdn.ven4tools.ru/version.json";
        private const string VersionSignatureUrl = "https://cdn.ven4tools.ru/version.json.sig";

        // Короткий таймаут: если CDN не ответил быстро — молча возвращаем null
        // и вызывающий код переключается на GitHub.
        private const int TimeoutSeconds = 5;

        // Единый HttpClient на весь процесс: пересоздание экземпляра в каждом
        // CdnService приводит к утечке сокетов (socket exhaustion). Заголовки и
        // таймаут задаются один раз — как в GitHubService и NotificationService.
        private static readonly HttpClient _httpClient = CreateClient();

        // Последний известный IP CDN из подписанного version.json (top-level cdn_ip).
        // Кэш на весь процесс: следующая IP-pinned попытка использует актуальный адрес,
        // а не только захардкоженный FallbackCdnIp. Заполняется при успешном разборе
        // манифеста; сидируется из настроек при старте (SeedLastKnownCdnIp).
        private static volatile string? _lastKnownCdnIp;

        /// <summary>Последний известный IP CDN (или null, если ещё не получен).</summary>
        public static string? LastKnownCdnIp => _lastKnownCdnIp;

        /// <summary>
        /// Заполнить кэш IP из сохранённых настроек при старте лаунчера — чтобы уже
        /// первая IP-pinned попытка (если домен заблокирован по DNS) шла на актуальный
        /// адрес прошлого запуска, а не только на захардкоженный FallbackCdnIp.
        /// </summary>
        public static void SeedLastKnownCdnIp(string? ip)
        {
            if (IsValidIp(ip)) _lastKnownCdnIp = ip;
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
            client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools.Launcher");
            return client;
        }

        public CdnService()
        {
        }

        /// <summary>
        /// Запрашивает version.json с CDN. Возвращает null при любой ошибке
        /// (CDN недоступен, таймаут, битый JSON) — это сигнал для тихого fallback на GitHub.
        ///
        /// Устойчивость к блокировке домена по DNS: если обычный (DNS) запрос падает
        /// именно из-за резолвинга имени (RKN может резать домен отдельно от IP) —
        /// повторяем попытку через IP-pinning (IpPinnedHttpClientFactory) на последний
        /// известный cdn_ip (иначе FallbackCdnIp), минуя DNS. TLS/SNI/проверка
        /// сертификата при этом остаются штатными — см. IpPinnedHttpClientFactory.
        ///
        /// Fail-closed по подписи: version.json — единственный источник и для URL
        /// загрузки, и для его же SHA256, поэтому компрометация CDN без проверки
        /// подписи означала бы, что оба контроля целостности подделываются
        /// одновременно (HIGH-находка аудита 2026-07-13). Манифест без валидной
        /// ECDSA-подписи (version.json.sig) отклоняется так же, как недоступный
        /// CDN — вызывающий код тихо переключается на GitHub, который не зависит
        /// от возможно скомпрометированного CDN.
        /// </summary>
        public async Task<CdnVersionInfo?> GetVersionInfoAsync(CancellationToken token = default)
        {
            // Первая попытка — обычный DNS-путь.
            try
            {
                var info = await FetchAndVerifyAsync(_httpClient, token);
                CacheCdnIp(info?.CdnIp);
                return info;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                // Не ошибка резолвинга DNS — обычный тихий fallback на GitHub.
                if (!IsDnsResolutionFailure(ex)) return null;
            }

            // Сюда попадаем только при ошибке резолвинга DNS домена cdn.ven4tools.ru:
            // повторяем через прямой IP в обход DNS.
            try
            {
                string ip = _lastKnownCdnIp ?? IpPinnedHttpClientFactory.FallbackCdnIp;
                var pinned = IpPinnedHttpClientFactory.GetOrCreate(ip, TimeSpan.FromSeconds(TimeoutSeconds));
                var info = await FetchAndVerifyAsync(pinned, token);
                CacheCdnIp(info?.CdnIp);
                return info;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Скачивает version.json + подпись указанным клиентом и проверяет ECDSA.
        /// Возвращает распарсенный манифест или null, если подпись невалидна.
        /// Сетевые ошибки ПРОБРАСЫВАЮТСЯ — вызывающий решает про IP-fallback.
        /// </summary>
        private static async Task<CdnVersionInfo?> FetchAndVerifyAsync(HttpClient client, CancellationToken token)
        {
            string json = await client.GetStringAsync(VersionUrl, token);
            string signature = await client.GetStringAsync(VersionSignatureUrl, token);
            if (!UpdateManifestVerifier.Verify(json, signature))
                return null;
            return JsonSerializer.Deserialize<CdnVersionInfo>(json);
        }

        /// <summary>
        /// Ошибка резолвинга DNS-имени: в .NET 8 это HttpRequestException с
        /// HttpRequestError.NameResolutionError, а в обёрнутом виде — SocketException
        /// с HostNotFound/TryAgain/NoData. Проверяем оба пути.
        /// Чистая функция без сети — покрыта unit-тестами.
        /// </summary>
        internal static bool IsDnsResolutionFailure(Exception ex)
        {
            if (ex is HttpRequestException httpEx &&
                httpEx.HttpRequestError == HttpRequestError.NameResolutionError)
                return true;

            for (Exception? cur = ex; cur != null; cur = cur.InnerException)
            {
                if (cur is SocketException se &&
                    (se.SocketErrorCode == SocketError.HostNotFound ||
                     se.SocketErrorCode == SocketError.TryAgain ||
                     se.SocketErrorCode == SocketError.NoData))
                    return true;
            }
            return false;
        }

        private static void CacheCdnIp(string? ip)
        {
            if (IsValidIp(ip)) _lastKnownCdnIp = ip;
        }

        private static bool IsValidIp(string? ip) =>
            !string.IsNullOrWhiteSpace(ip) && IPAddress.TryParse(ip, out _);

        public void Dispose()
        {
            // HttpClient — статический singleton, разделяется между всеми
            // экземплярами и живёт весь процесс. Здесь его не освобождаем.
        }
    }
}
