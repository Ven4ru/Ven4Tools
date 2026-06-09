using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    internal static class PresetService
    {
        private const string ApiBase = ApiConfig.DbApi;
        private static readonly string LocalPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Ven4Tools", "presets.json");

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

        private static HttpRequestMessage Req(HttpMethod method, string url)
        {
            var req = new HttpRequestMessage(method, url);
            if (!string.IsNullOrEmpty(UserSession.Token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", UserSession.Token);
            return req;
        }

        // ── Загрузить пресеты (сервер если авторизован, иначе локал) ─────────────

        public static async Task<List<Preset>> LoadAsync(int? userId)
        {
            if (userId.HasValue && !string.IsNullOrEmpty(UserSession.Token))
            {
                try
                {
                    var req = Req(HttpMethod.Get, $"{ApiBase}?action=get_presets");
                    var resp = await _http.SendAsync(req);
                    var json = await resp.Content.ReadAsStringAsync();
                    var list = JsonConvert.DeserializeObject<List<Preset>>(json);
                    return list ?? new();
                }
                catch { }
            }
            return LoadLocal();
        }

        // ── Сохранить пресет ─────────────────────────────────────────────────────

        public static async Task<Preset?> SaveAsync(int? userId, Preset preset)
        {
            if (userId.HasValue && !string.IsNullOrEmpty(UserSession.Token))
            {
                try
                {
                    var body = JsonConvert.SerializeObject(new
                    {
                        name        = preset.Name,
                        description = preset.Description,
                        apps        = preset.Apps
                    });
                    var req = Req(HttpMethod.Post, $"{ApiBase}?action=save_preset");
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    var resp = await _http.SendAsync(req);
                    var json = await resp.Content.ReadAsStringAsync();
                    var r = JObject.Parse(json);
                    if (r["success"]?.Value<bool>() == true)
                    {
                        var p = r["preset"]?.ToObject<Preset>();
                        if (p != null) preset.Id = p.Id;
                        return preset;
                    }
                }
                catch { }
            }

            // fallback — локальное хранение
            var local = LoadLocal();
            if (preset.Id == 0)
            {
                preset.Id = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                preset.IsLocal = true;
                local.Add(preset);
            }
            else
            {
                var idx = local.FindIndex(p => p.Id == preset.Id);
                if (idx >= 0) local[idx] = preset;
                else local.Add(preset);
            }
            SaveLocal(local);
            return preset;
        }

        // ── Удалить пресет ────────────────────────────────────────────────────────

        public static async Task DeleteAsync(int? userId, Preset preset)
        {
            if (userId.HasValue && !preset.IsLocal && !string.IsNullOrEmpty(UserSession.Token))
            {
                try
                {
                    var body = JsonConvert.SerializeObject(new { preset_id = preset.Id });
                    var req = Req(HttpMethod.Post, $"{ApiBase}?action=delete_preset");
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    await _http.SendAsync(req);
                    return;
                }
                catch { }
            }
            var local = LoadLocal();
            local.RemoveAll(p => p.Id == preset.Id);
            SaveLocal(local);
        }

        // ── Поделиться ────────────────────────────────────────────────────────────

        public static async Task<string?> ShareAsync(int userId, int presetId)
        {
            try
            {
                var body = JsonConvert.SerializeObject(new { preset_id = presetId });
                var req = Req(HttpMethod.Post, $"{ApiBase}?action=share_preset");
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                var resp = await _http.SendAsync(req);
                var json = await resp.Content.ReadAsStringAsync();
                var r = JObject.Parse(json);
                return r["success"]?.Value<bool>() == true ? r["share_code"]?.ToString() : null;
            }
            catch { return null; }
        }

        // ── Загрузить по коду ─────────────────────────────────────────────────────

        public static async Task<Preset?> GetByCodeAsync(string code)
        {
            try
            {
                var json = await _http.GetStringAsync(
                    $"{ApiBase}?action=get_preset_by_code&code={Uri.EscapeDataString(code.Trim())}");
                var r = JObject.Parse(json);
                if (r["error"] != null) return null;
                return new Preset
                {
                    Id          = r["id"]?.Value<int>() ?? 0,
                    Name        = r["name"]?.ToString() ?? "",
                    Description = r["description"]?.ToString() ?? "",
                    Apps        = r["apps"]?.ToObject<List<string>>() ?? new(),
                    ShareCode   = r["share_code"]?.ToString()
                };
            }
            catch { return null; }
        }

        // ── Локальное хранение ────────────────────────────────────────────────────

        private static List<Preset> LoadLocal()
        {
            try
            {
                if (!File.Exists(LocalPath)) return new();
                var json = File.ReadAllText(LocalPath, Encoding.UTF8);
                return JsonConvert.DeserializeObject<List<Preset>>(json) ?? new();
            }
            catch { return new(); }
        }

        private static void SaveLocal(List<Preset> list)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LocalPath)!);
                File.WriteAllText(LocalPath, JsonConvert.SerializeObject(list, Formatting.Indented), Encoding.UTF8);
            }
            catch { }
        }
    }
}
