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
        /// </summary>
        public async Task<CdnVersionInfo?> GetVersionInfoAsync(CancellationToken token = default)
        {
            try
            {
                string json = await _httpClient.GetStringAsync(VersionUrl, token);
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
