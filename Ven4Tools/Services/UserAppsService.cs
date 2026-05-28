using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public class UserAppsService
    {
        private const string ApiBase = "https://www.ven4tools.ru/api/db.php";
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        public async Task<List<AppInfo>> FetchAsync(int userId)
        {
            try
            {
                var json = await _http.GetStringAsync($"{ApiBase}?action=get_user_apps&user_id={userId}");
                var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("apps", out var arr)) return new();

                return arr.EnumerateArray().Select(ParseApp).ToList();
            }
            catch { return new(); }
        }

        public async Task SaveAsync(int userId, AppInfo app)
        {
            try
            {
                var payload = new
                {
                    user_id = userId,
                    app_id = app.Id,
                    display_name = app.DisplayName,
                    category = app.Category.ToString(),
                    installer_urls = JsonSerializer.Serialize(app.InstallerUrls),
                    winget_id = app.AlternativeId,
                    silent_args = app.SilentArgs,
                    required_space_mb = app.RequiredSpaceMB
                };
                var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                await _http.PostAsync($"{ApiBase}?action=save_user_app", body);
            }
            catch { }
        }

        public async Task DeleteAsync(int userId, string appId)
        {
            try
            {
                var payload = new { user_id = userId, app_id = appId };
                var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                await _http.PostAsync($"{ApiBase}?action=delete_user_app", body);
            }
            catch { }
        }

        private AppInfo ParseApp(JsonElement item)
        {
            string categoryStr = item.TryGetProperty("category", out var cat) ? cat.GetString() ?? "" : "";
            string urlsJson    = item.TryGetProperty("installer_urls", out var u) ? u.GetString() ?? "[]" : "[]";

            List<string> urls;
            try { urls = JsonSerializer.Deserialize<List<string>>(urlsJson) ?? new(); }
            catch { urls = new(); }

            return new AppInfo
            {
                Id             = item.TryGetProperty("app_id", out var id)   ? id.GetString()   ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString(),
                DisplayName    = item.TryGetProperty("display_name", out var n) ? n.GetString() ?? "" : "",
                Category       = ParseCategory(categoryStr),
                InstallerUrls  = urls,
                AlternativeId  = item.TryGetProperty("winget_id", out var wid)  ? wid.GetString() : null,
                SilentArgs     = item.TryGetProperty("silent_args", out var sa)  ? sa.GetString() ?? "/S" : "/S",
                RequiredSpaceMB = item.TryGetProperty("required_space_mb", out var mb) ? mb.GetInt64() : 100,
                IsUserAdded    = true
            };
        }

        private static AppCategory ParseCategory(string s) => s switch
        {
            "Браузеры"        => AppCategory.Браузеры,
            "Офис"            => AppCategory.Офис,
            "Графика"         => AppCategory.Графика,
            "Разработка"      => AppCategory.Разработка,
            "Мессенджеры"     => AppCategory.Мессенджеры,
            "Мультимедиа"     => AppCategory.Мультимедиа,
            "Системные"       => AppCategory.Системные,
            "Пользовательские"=> AppCategory.Пользовательские,
            _                 => AppCategory.Другое
        };
    }
}
