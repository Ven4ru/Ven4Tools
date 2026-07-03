using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Models;
using CatalogApp = Ven4Tools.Models.App;

namespace Ven4Tools.Services
{
    public static class OfflineService
    {
        private const string CacheFolderName = "Ven4ToolsCache";
        private const string CacheMarkerName = ".ven4tools-cache";
        private const string CacheMarkerContent = "Ven4Tools offline cache v1";

        public static bool IsOffline => ProfileService.Current.OfflineMode;

        public static string CacheBasePath
        {
            get
            {
                var custom = ProfileService.Current.OfflineCachePath;
                return !string.IsNullOrWhiteSpace(custom)
                    ? custom
                    : Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Ven4Tools");
            }
        }

        public static string CachePath => Path.Combine(CacheBasePath, CacheFolderName);

        // ── Cache queries ─────────────────────────────────────────────────────────

        public static bool HasCachedInstaller(string appId)
        {
            try
            {
                var dir = CachePath;
                if (!Directory.Exists(dir)) return false;
                return Directory.GetFiles(dir, $"{SanitizeId(appId)}.*").Length > 0;
            }
            catch { return false; }
        }

        public static string? GetCachedInstallerPath(string appId)
        {
            try
            {
                var dir = CachePath;
                if (!Directory.Exists(dir)) return null;
                return Directory.GetFiles(dir, $"{SanitizeId(appId)}.*").FirstOrDefault();
            }
            catch { return null; }
        }

        public static long GetCachedInstallerSizeMB(string appId)
        {
            var path = GetCachedInstallerPath(appId);
            if (path == null) return 0;
            try { return new FileInfo(path).Length / (1024 * 1024); }
            catch { return 0; }
        }

        public static (int count, long sizeMB) GetCacheStats()
        {
            try
            {
                var dir = CachePath;
                if (!Directory.Exists(dir)) return (0, 0);
                var files = GetCachePayloadFiles(dir);
                long size = files.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
                return (files.Length, size / (1024 * 1024));
            }
            catch { return (0, 0); }
        }

        public static void ClearCache()
        {
            try
            {
                var dir = CachePath;
                if (!Directory.Exists(dir)) return;
                if (!HasValidMarker(dir))
                {
                    AppLogger.Write($"[OfflineService] Очистка отменена: папка не является кэшем Ven4Tools: {dir}");
                    return;
                }
                foreach (var file in GetCachePayloadFiles(dir))
                    try { File.Delete(file); } catch { }
            }
            catch (Exception ex) { AppLogger.Write($"[OfflineService] Очистка офлайн-кэша: {ex.Message}"); }
        }

        public static void EnsureCacheDir()
        {
            var dir = CachePath;
            Directory.CreateDirectory(dir);
            var marker = Path.Combine(dir, CacheMarkerName);
            if (!File.Exists(marker))
                File.WriteAllText(marker, CacheMarkerContent);
        }

        // ── Downloading ───────────────────────────────────────────────────────────

        public static async Task<bool> CacheInstallerDirectAsync(
            CatalogApp app, HttpClient http,
            IProgress<(string status, int pct)>? progress,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(app.DownloadUrl)) return false;
            if (!DownloadValidator.ValidateUrl(app.DownloadUrl)) return false;
            if (!HashHelper.HasExpectedHash(app.Sha256)) return false;

            EnsureCacheDir();
            string ext  = app.DownloadUrl.Contains(".msi", StringComparison.OrdinalIgnoreCase) ? ".msi" : ".exe";
            string dest = Path.Combine(CachePath, SanitizeId(app.Id) + ext);
            string partial = dest + ".partial";

            progress?.Report(($"⬇️ {app.Name}...", 0));
            try
            {
                using var resp = await http.GetAsync(app.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
                resp.EnsureSuccessStatusCode();
                if (!DownloadValidator.ValidateAfterRedirect(resp))
                    throw new InvalidOperationException("Редирект загрузки ведёт не на HTTPS");
                var total = resp.Content.Headers.ContentLength ?? -1L;
                var read  = 0L;
                var buf   = new byte[81920];

                await using var fs = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
                await using var stream = await resp.Content.ReadAsStreamAsync(token);
                int bytes;
                while ((bytes = await stream.ReadAsync(buf.AsMemory(), token)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, bytes), token);
                    read += bytes;
                    if (total > 0)
                    {
                        int pct = (int)((double)read / total * 100);
                        progress?.Report(($"⬇️ {app.Name}: {pct}%", pct));
                    }
                }
                await fs.FlushAsync(token);
                await fs.DisposeAsync();

                if (!await HashHelper.VerifyHashAsync(partial, app.Sha256!))
                {
                    progress?.Report(($"❌ {app.Name}: SHA256 не совпадает — файл повреждён", 100));
                    try { File.Delete(partial); } catch { }
                    return false;
                }

                File.Move(partial, dest, true);
                progress?.Report(($"✅ {app.Name}", 100));
                return true;
            }
            catch (OperationCanceledException)
            {
                try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[OfflineService] Кэширование установщика «{app.Name}» (прямая ссылка): {ex.Message}");
                progress?.Report(($"❌ {app.Name}: {ex.Message}", 0));
                try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                return false;
            }
        }

        private static readonly HashSet<char> _invalidChars =
            new(Path.GetInvalidFileNameChars());

        private static bool HasValidMarker(string dir)
        {
            try
            {
                var marker = Path.Combine(dir, CacheMarkerName);
                return File.Exists(marker) &&
                       File.ReadAllText(marker).Equals(CacheMarkerContent, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static string[] GetCachePayloadFiles(string dir) =>
            Directory.GetFiles(dir)
                .Where(f =>
                {
                    var ext = Path.GetExtension(f);
                    return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                           ext.Equals(".msi", StringComparison.OrdinalIgnoreCase) ||
                           ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
                           ext.Equals(".partial", StringComparison.OrdinalIgnoreCase);
                })
                .ToArray();

        private static string SanitizeId(string id) =>
            string.Concat(id.Select(c => _invalidChars.Contains(c) ? '_' : c));
    }
}
