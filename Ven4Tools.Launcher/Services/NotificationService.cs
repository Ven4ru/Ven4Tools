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

        public static async Task<Notification?> GetLatestAsync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools-Launcher");
                var url  = $"{Url}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                var json = await http.GetStringAsync(url);
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
