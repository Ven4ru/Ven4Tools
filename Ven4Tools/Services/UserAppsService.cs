using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public class UserAppsService
    {
        private const string ApiBase = ApiConfig.DbApi;
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // Формируем запрос с токеном авторизации. Сервер сам определяет
        // пользователя по Bearer-токену — user_id с клиента не передаётся
        // (защита от IDOR).
        private static HttpRequestMessage Req(HttpMethod method, string url)
        {
            var req = new HttpRequestMessage(method, url);
            if (!string.IsNullOrEmpty(UserSession.Token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", UserSession.Token);
            return req;
        }

        public async Task<List<AppInfo>> FetchAsync()
        {
            try
            {
                using var req = Req(HttpMethod.Get, $"{ApiBase}?action=get_user_apps");
                using var resp = await _http.SendAsync(req);
                var json = await resp.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("apps", out var arr)) return new();

                return arr.EnumerateArray().Select(ParseApp).ToList();
            }
            catch (Exception ex) { AppLogger.Write($"[UserAppsService] {ex.Message}"); return new(); }
        }

        public async Task SaveAsync(AppInfo app)
        {
            try
            {
                var payload = new
                {
                    app_id = app.Id,
                    display_name = app.DisplayName,
                    category = app.Category.ToString(),
                    installer_urls = JsonSerializer.Serialize(app.InstallerUrls),
                    winget_id = app.AlternativeId,
                    silent_args = app.SilentArgs,
                    required_space_mb = app.RequiredSpaceMB
                };
                using var req = Req(HttpMethod.Post, $"{ApiBase}?action=save_user_app");
                req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                await _http.SendAsync(req);
            }
            catch (Exception ex) { AppLogger.Write($"[UserAppsService] {ex.Message}"); }
        }

        public async Task DeleteAsync(string appId)
        {
            try
            {
                var payload = new { app_id = appId };
                using var req = Req(HttpMethod.Post, $"{ApiBase}?action=delete_user_app");
                req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                await _http.SendAsync(req);
            }
            catch (Exception ex) { AppLogger.Write($"[UserAppsService] {ex.Message}"); }
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
                Category       = AppCategoryHelper.Parse(categoryStr),
                InstallerUrls  = urls,
                AlternativeId  = item.TryGetProperty("winget_id", out var wid)  ? wid.GetString() : null,
                SilentArgs     = item.TryGetProperty("silent_args", out var sa)  ? sa.GetString() ?? "/S" : "/S",
                RequiredSpaceMB = item.TryGetProperty("required_space_mb", out var mb) ? mb.GetInt64() : 100,
                IsUserAdded    = true
            };
        }
    }
}
