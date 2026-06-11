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
        public static bool IsOffline => ProfileService.Current.OfflineMode;

        public static string CachePath
        {
            get
            {
                var custom = ProfileService.Current.OfflineCachePath;
                return !string.IsNullOrWhiteSpace(custom)
                    ? custom
                    : Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Ven4Tools", "offline_cache");
            }
        }

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
                var files = Directory.GetFiles(dir);
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
                foreach (var file in Directory.GetFiles(dir))
                    try { File.Delete(file); } catch { }
            }
            catch { }
        }

        public static void EnsureCacheDir() =>
            Directory.CreateDirectory(CachePath);

        // ── Downloading ───────────────────────────────────────────────────────────

        public static async Task<bool> CacheInstallerDirectAsync(
            CatalogApp app, HttpClient http,
            IProgress<(string status, int pct)>? progress,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(app.DownloadUrl)) return false;

            EnsureCacheDir();
            string ext  = app.DownloadUrl.Contains(".msi", StringComparison.OrdinalIgnoreCase) ? ".msi" : ".exe";
            string dest = Path.Combine(CachePath, SanitizeId(app.Id) + ext);

            progress?.Report(($"⬇️ {app.Name}...", 0));
            try
            {
                using var resp = await http.GetAsync(app.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? -1L;
                var read  = 0L;
                var buf   = new byte[81920];

                await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
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

                if (!string.IsNullOrWhiteSpace(app.Sha256))
                {
                    progress?.Report(($"🔍 Проверка SHA256...", 95));
                    bool hashOk = await HashHelper.VerifyHashAsync(dest, app.Sha256);
                    if (!hashOk)
                    {
                        progress?.Report(($"❌ {app.Name}: SHA256 не совпадает — файл повреждён", 100));
                        try { File.Delete(dest); } catch { }
                        return false;
                    }
                }

                progress?.Report(($"✅ {app.Name}", 100));
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                progress?.Report(($"❌ {app.Name}: {ex.Message}", 0));
                try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                return false;
            }
        }

        public static async Task<bool> CacheInstallerWingetAsync(
            CatalogApp app,
            IProgress<(string status, int pct)>? progress,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(app.WingetId)) return false;

            EnsureCacheDir();
            progress?.Report(($"📦 winget download {app.Name}...", 0));
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName               = "winget",
                    Arguments              = $"download --id \"{app.WingetId}\" -e --source winget --directory \"{CachePath}\" --accept-package-agreements --accept-source-agreements",
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };
                using var p = new System.Diagnostics.Process { StartInfo = psi };
                p.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        progress?.Report(($"  {e.Data}", -1));
                };
                p.ErrorDataReceived += (_, _) => { };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                while (!p.HasExited)
                {
                    if (token.IsCancellationRequested)
                    {
                        try { p.Kill(entireProcessTree: true); } catch { }
                        token.ThrowIfCancellationRequested();
                    }
                    await Task.Delay(150, token);
                }

                bool ok = p.ExitCode == 0;
                if (ok)
                {
                    // winget сохраняет установщик под собственным именем («Имя пакета X.Y.Z.exe»),
                    // а кэш ищет файлы по шаблону SanitizeId(appId).*. Переименовываем самый
                    // свежий установщик в ожидаемое имя и убираем служебные .yaml-манифесты.
                    try
                    {
                        var installerExts = new[] { ".exe", ".msi" };
                        var newest = Directory.GetFiles(CachePath)
                            .Where(f => installerExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                            .OrderByDescending(File.GetCreationTimeUtc)
                            .FirstOrDefault();
                        if (newest != null)
                        {
                            var target = Path.Combine(CachePath, SanitizeId(app.Id) + Path.GetExtension(newest));
                            if (!string.Equals(newest, target, StringComparison.OrdinalIgnoreCase))
                            {
                                if (File.Exists(target)) File.Delete(target);
                                File.Move(newest, target);
                            }
                        }
                        foreach (var yaml in Directory.GetFiles(CachePath, "*.yaml"))
                            try { File.Delete(yaml); } catch { }
                    }
                    catch { /* переименование/очистка — best-effort */ }
                }
                progress?.Report((ok ? $"✅ {app.Name} (winget)" : $"⚠️ {app.Name}: код {p.ExitCode}", ok ? 100 : 0));
                return ok;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { progress?.Report(($"❌ {app.Name}: {ex.Message}", 0)); return false; }
        }

        private static readonly HashSet<char> _invalidChars =
            new(Path.GetInvalidFileNameChars());

        private static string SanitizeId(string id) =>
            string.Concat(id.Select(c => _invalidChars.Contains(c) ? '_' : c));
    }
}
