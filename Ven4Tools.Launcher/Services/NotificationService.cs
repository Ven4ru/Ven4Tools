using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services
{
    public static class NotificationService
    {
        private const string Url =
            "https://raw.githubusercontent.com/Ven4ru/Ven4Tools/main/Catalog/notifications.json";
        private const string SignatureUrl = Url + ".sig";

        // Один HttpClient на всё время жизни процесса: создание нового клиента
        // на каждый вызов исчерпывает сокеты (socket exhaustion)
        private static readonly HttpClient _http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools-Launcher");
            return client;
        }

        public static async Task<Notification?> GetLatestAsync()
        {
            try
            {
                var cacheBust = $"?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                var json      = await _http.GetStringAsync(Url + cacheBust);
                var signature = await _http.GetStringAsync(SignatureUrl + cacheBust);

                // Fail-closed: без валидной ECDSA-подписи уведомление не показываем —
                // компрометация только хостинга (без приватного ключа, который
                // никогда не покидает офлайн-машину) не даёт подделать текст.
                if (!NotificationsVerifier.Verify(json, signature))
                    return null;

                var root = JObject.Parse(json);
                var first = (root["notifications"] as JArray)?.First as JObject;
                if (first == null) return null;
                return new Notification
                {
                    Id      = first["id"]?.ToString()      ?? "",
                    Title   = first["title"]?.ToString()   ?? "Ven4Tools",
                    Message = first["message"]?.ToString() ?? "",
                    Type    = first["type"]?.ToString()    ?? "info"
                };
            }
            catch { return null; }
        }
    }
}
