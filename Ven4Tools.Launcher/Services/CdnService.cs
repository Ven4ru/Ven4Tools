// Сервис получения информации о версиях и ссылок на загрузку с CDN.
// CDN — основной источник, GitHub — резервный.
using System;
using System.Net.Http;
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
            try
            {
                string json = await _httpClient.GetStringAsync(VersionUrl, token);
                string signature = await _httpClient.GetStringAsync(VersionSignatureUrl, token);
                if (!UpdateManifestVerifier.Verify(json, signature))
                    return null;
                return JsonSerializer.Deserialize<CdnVersionInfo>(json);
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            // HttpClient — статический singleton, разделяется между всеми
            // экземплярами и живёт весь процесс. Здесь его не освобождаем.
        }
    }
}
