using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services
{
    public static class NotificationService
    {
        // Раньше был единственный источник — raw.githubusercontent.com, который для
        // основной аудитории проекта (РФ) фактически недоступен (блокировка по SNI,
        // см. reference_github_rkn_blocking) — канал уведомлений владельца молчал
        // бесшумно. Порядок источников — тот же, что у остального лаунчера
        // (CdnService/GitHubService): CDN → зеркало на хостинге → GitHub напрямую.
        private static readonly string[] Sources =
        {
            "https://cdn.ven4tools.ru/notifications.json",
            "https://ven4tools.ru/catalog/notifications.json",
            "https://raw.githubusercontent.com/Ven4ru/Ven4Tools/main/Catalog/notifications.json",
        };

        // Один HttpClient на всё время жизни процесса: создание нового клиента
        // на каждый вызов исчерпывает сокеты (socket exhaustion)
        private static readonly HttpClient _http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools-Launcher");
            return client;
        }

        /// <param name="log">
        /// Необязательный колбэк для диагностики — раньше любая причина отказа
        /// (недоступный источник, невалидная подпись) тонула в пустом catch без следа.
        /// </param>
        public static async Task<Notification?> GetLatestAsync(Action<string>? log = null)
        {
            foreach (var url in Sources)
            {
                try
                {
                    var cacheBust = $"?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                    var json      = await _http.GetStringAsync(url + cacheBust);
                    var signature = await _http.GetStringAsync(url + ".sig" + cacheBust);

                    // Fail-closed: без валидной ECDSA-подписи уведомление не показываем —
                    // компрометация только хостинга (без приватного ключа, который
                    // никогда не покидает офлайн-машину) не даёт подделать текст.
                    if (!NotificationsVerifier.Verify(json, signature))
                    {
                        log?.Invoke($"[NotificationService] {url}: подпись невалидна, пробуем следующий источник.");
                        continue;
                    }

                    var root = JObject.Parse(json);
                    var first = (root["notifications"] as JArray)?.First as JObject;
                    // Валидно подписанный пустой список — легитимный ответ "уведомлений
                    // нет", а не сбой источника: на этом останавливаемся, не перебираем
                    // остальные источники дальше ради того же самого ответа.
                    if (first == null) return null;
                    return new Notification
                    {
                        Id      = first["id"]?.ToString()      ?? "",
                        Title   = first["title"]?.ToString()   ?? "Ven4Tools",
                        Message = first["message"]?.ToString() ?? "",
                        Type    = first["type"]?.ToString()    ?? "info"
                    };
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[NotificationService] {url}: недоступен ({ex.Message}).");
                }
            }

            log?.Invoke("[NotificationService] Ни один источник не ответил.");
            return null;
        }
    }
}
