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
                catch (Exception ex) { AppLogger.Write($"[PresetService] LoadAsync: {ex.Message}"); }
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
                catch (Exception ex) { AppLogger.Write($"[PresetService] SaveAsync: {ex.Message}"); }
            }

            // fallback — локальное хранение.
            // Новый пресет (Id == 0) НЕ помечаем IsLocal: пользователь авторизован и хотел
            // облачный пресет, просто сеть подвела. Ставим NeedsSync и храним под отрицательным
            // Id — при следующем UpdateAsync пресет будет создан на сервере (POST).
            // Существующий облачный пресет (Id > 0) IsLocal не трогаем: он точно есть на
            // сервере, и при следующем сохранении retry снова попадёт в облако.
            var local = LoadLocal();
            if (preset.Id == 0)
            {
                // Если пользователь авторизован — это «отложенный облачный» пресет (NeedsSync).
                // Если нет — обычный локальный пресет.
                bool loggedIn = userId.HasValue && !string.IsNullOrEmpty(UserSession.Token);
                preset.IsLocal   = !loggedIn;
                preset.NeedsSync = loggedIn;
                // Отрицательные ID для локальных пресетов: не пересекаются с серверными (положительные auto-increment)
                int newId;
                do { newId = Random.Shared.Next(int.MinValue + 1, 0); } while (local.Exists(p => p.Id == newId));
                preset.Id = newId;
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

        public static async Task<bool> DeleteAsync(int? userId, Preset preset)
        {
            if (userId.HasValue && !preset.IsLocal && !string.IsNullOrEmpty(UserSession.Token))
            {
                try
                {
                    var body = JsonConvert.SerializeObject(new { preset_id = preset.Id });
                    var req = Req(HttpMethod.Post, $"{ApiBase}?action=delete_preset");
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    var resp = await _http.SendAsync(req);
                    var json = await resp.Content.ReadAsStringAsync();
                    var r = JObject.Parse(json);
                    // При неуспехе сервера локальную копию не трогаем
                    return r["success"]?.Value<bool>() == true;
                }
                catch (Exception ex) { AppLogger.Write($"[PresetService] DeleteAsync: {ex.Message}"); return false; }
            }
            var local = LoadLocal();
            local.RemoveAll(p => p.Id == preset.Id);
            SaveLocal(local);
            return true;
        }

        // ── Обновить пресет (имя / описание / состав) ─────────────────────────────

        public static async Task<bool> UpdateAsync(int? userId, Preset preset)
        {
            if (userId.HasValue && !preset.IsLocal && !string.IsNullOrEmpty(UserSession.Token))
            {
                // Отрицательный Id у не-локального пресета означает «отложенную синхронизацию»:
                // пресет создавался офлайн и в облако ещё не попал. Обновлять его по
                // несуществующему preset_id нельзя — создаём на сервере как новый (POST).
                if (preset.Id < 0)
                {
                    int oldLocalId = preset.Id;
                    try
                    {
                        var createBody = JsonConvert.SerializeObject(new
                        {
                            name        = preset.Name,
                            description = preset.Description,
                            apps        = preset.Apps
                        });
                        var createReq = Req(HttpMethod.Post, $"{ApiBase}?action=save_preset");
                        createReq.Content = new StringContent(createBody, Encoding.UTF8, "application/json");
                        var createResp = await _http.SendAsync(createReq);
                        var createJson = await createResp.Content.ReadAsStringAsync();
                        var cr = JObject.Parse(createJson);
                        if (cr["success"]?.Value<bool>() == true)
                        {
                            var p = cr["preset"]?.ToObject<Preset>();
                            if (p != null) preset.Id = p.Id;
                            preset.NeedsSync = false;
                            // Пресет переехал в облако — убираем локальную копию с отрицательным Id.
                            var localAfterSync = LoadLocal();
                            localAfterSync.RemoveAll(x => x.Id == oldLocalId);
                            SaveLocal(localAfterSync);
                            return true;
                        }
                    }
                    catch (Exception ex) { AppLogger.Write($"❌ UpdateAsync(sync new): {ex.Message}"); }

                    // Сервер по-прежнему недоступен — обновляем локальную копию, ждём следующей попытки.
                    var localPending = LoadLocal();
                    var pi = localPending.FindIndex(x => x.Id == oldLocalId);
                    if (pi < 0) return false;
                    localPending[pi] = preset;
                    SaveLocal(localPending);
                    return true;
                }

                try
                {
                    var body = JsonConvert.SerializeObject(new
                    {
                        preset_id   = preset.Id,
                        name        = preset.Name,
                        description = preset.Description,
                        apps        = preset.Apps
                    });
                    var req = Req(HttpMethod.Post, $"{ApiBase}?action=update_preset");
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    var resp = await _http.SendAsync(req);
                    var json = await resp.Content.ReadAsStringAsync();
                    var r    = JObject.Parse(json);
                    return r["success"]?.Value<bool>() == true;
                }
                catch (Exception ex) { AppLogger.Write($"❌ UpdateAsync: {ex.Message}"); return false; }
            }

            // Локальный fallback
            var local = LoadLocal();
            var idx   = local.FindIndex(p => p.Id == preset.Id);
            if (idx < 0) return false;
            local[idx] = preset;
            SaveLocal(local);
            return true;
        }

        // ── Поделиться ────────────────────────────────────────────────────────────

        public static async Task<string?> ShareAsync(int presetId)
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
            catch (Exception ex) { AppLogger.Write($"[PresetService] ShareAsync: {ex.Message}"); return null; }
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
            catch (Exception ex) { AppLogger.Write($"[PresetService] GetByCodeAsync: {ex.Message}"); return null; }
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
            catch (Exception ex) { AppLogger.Write($"[PresetService] Чтение локальных пресетов: {ex.Message}"); return new(); }
        }

        private static void SaveLocal(List<Preset> list)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LocalPath)!);
                File.WriteAllText(LocalPath, JsonConvert.SerializeObject(list, Formatting.Indented), Encoding.UTF8);
            }
            catch (Exception ex) { AppLogger.Write($"❌ Сохранение локального файла пресетов: {ex.Message}"); }
        }
    }
}
