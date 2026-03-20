using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public class UpdateService : IDisposable
    {
        private readonly HttpClient httpClient;
        private readonly string repoOwner;
        private readonly string repoName;
        private readonly string currentVersion;
        private readonly IProgress<UpdateProgress>? progress;

        public class UpdateProgress
        {
            public string Status { get; set; } = string.Empty;
            public int Percentage { get; set; }
            public long BytesDownloaded { get; set; }
            public long TotalBytes { get; set; }
        }

        public UpdateService(string repoOwner, string repoName, IProgress<UpdateProgress>? progress = null)
        {
            this.repoOwner = repoOwner;
            this.repoName = repoName;
            this.progress = progress;

            httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools");
            httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
            httpClient.Timeout = TimeSpan.FromSeconds(15);

            var version = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName()
                .Version;

            currentVersion = version != null 
                ? $"{version.Major}.{version.Minor}.{version.Build}" 
                : "2.2.2";
        }

        public async Task<UpdateInfo> CheckForUpdateAsync()
        {
            var result = new UpdateInfo
            {
                CurrentVersion = currentVersion,
                HasUpdate = false
            };

            try
            {
                ReportProgress("🔍 Проверка обновлений...", 10);
                string apiUrl = $"https://api.github.com/repos/{repoOwner}/{repoName}/releases/latest";

                using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                using var response = await httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    result.Error = $"Ошибка подключения к GitHub (код {response.StatusCode})";
                    return result;
                }

                string json = await response.Content.ReadAsStringAsync();
                var release = JsonSerializer.Deserialize<GitHubRelease>(json);

                if (release == null)
                {
                    result.Error = "Не удалось прочитать информацию о релизе";
                    return result;
                }

                var asset = release.assets.FirstOrDefault(a => 
                    a.name.Contains("Setup", StringComparison.OrdinalIgnoreCase) && 
                    a.name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

                if (asset == null)
                {
                    result.Error = "Установщик не найден в релизе";
                    return result;
                }

                string latestVersion = release.tag_name.TrimStart('v');

                if (CompareVersions(latestVersion, currentVersion) > 0)
                {
                    result.HasUpdate = true;
                    result.LatestVersion = latestVersion;
                    result.DownloadUrl = asset.browser_download_url;
                    result.ReleaseNotes = release.body;
                    result.ReleaseDate = release.published_at;
                    result.FileSize = asset.size;
                    result.Priority = DeterminePriority(release.body);

                    ReportProgress($"✅ Найдена версия {latestVersion}", 50);
                }

                return result;
            }
            catch (Exception ex)
            {
                result.Error = $"Ошибка: {ex.Message}";
                return result;
            }
        }

        private UpdatePriority DeterminePriority(string? releaseNotes)
        {
            if (string.IsNullOrEmpty(releaseNotes))
                return UpdatePriority.Minor;

            var lowerNotes = releaseNotes.ToLowerInvariant();

            if (lowerNotes.Contains("[critical]") || lowerNotes.Contains("🔴") ||
                lowerNotes.Contains("критическое") || lowerNotes.Contains("security"))
                return UpdatePriority.Critical;

            if (lowerNotes.Contains("[recommended]") || lowerNotes.Contains("🔸") ||
                lowerNotes.Contains("рекомендуется") || lowerNotes.Contains("улучшение"))
                return UpdatePriority.Recommended;

            return UpdatePriority.Minor;
        }

        private int CompareVersions(string v1, string v2)
        {
            var parts1 = v1.Split('.');
            var parts2 = v2.Split('.');

            for (int i = 0; i < Math.Max(parts1.Length, parts2.Length); i++)
            {
                int p1 = i < parts1.Length && int.TryParse(parts1[i], out var n1) ? n1 : 0;
                int p2 = i < parts2.Length && int.TryParse(parts2[i], out var n2) ? n2 : 0;

                if (p1 > p2) return 1;
                if (p1 < p2) return -1;
            }

            return 0;
        }

        public async Task<bool> DownloadAndInstallSilentlyAsync(UpdateInfo updateInfo,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(updateInfo.DownloadUrl))
                return false;

            string installerPath = Path.Combine(Path.GetTempPath(), $"Ven4Tools_Setup_{updateInfo.LatestVersion}.exe");

            try
            {
                ReportProgress("📥 Скачивание обновления...", 60);

                using var response = await httpClient.GetAsync(
                    updateInfo.DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                var bytesDownloaded = 0L;

                await using var contentStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(installerPath, FileMode.Create, FileAccess.Write);

                var buffer = new byte[8192];
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    bytesDownloaded += bytesRead;

                    if (totalBytes > 0)
                    {
                        int percentage = 60 + (int)((double)bytesDownloaded / totalBytes * 30);
                        ReportProgress($"📥 {bytesDownloaded / 1024 / 1024:F1} МБ / {totalBytes / 1024 / 1024:F1} МБ",
                            percentage, bytesDownloaded, totalBytes);
                    }
                }

                ReportProgress("✅ Скачивание завершено", 95);

                string currentExe = Process.GetCurrentProcess().MainModule!.FileName;
                string appDir = Path.GetDirectoryName(currentExe)!;
                string updaterDll = Path.Combine(appDir, "Ven4Tools.Updater.dll");

                if (!File.Exists(updaterDll))
                {
                    DebugLog($"❌ Апдейтер не найден: {updaterDll}");
                    return false;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"\"{updaterDll}\" \"{installerPath}\" /silent",
                    UseShellExecute = true,
                    CreateNoWindow = true
                });

                await Task.Delay(500);
                return true;
            }
            catch (Exception ex)
            {
                try { File.Delete(installerPath); } catch { }
                throw;
            }
        }

        private void ReportProgress(string status, int percentage, long downloaded = 0, long total = 0)
        {
            progress?.Report(new UpdateProgress
            {
                Status = status,
                Percentage = percentage,
                BytesDownloaded = downloaded,
                TotalBytes = total
            });
        }

        private void DebugLog(string message)
        {
            try
            {
                string logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Ven4Tools", "update_service.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} - {message}\n");
            }
            catch { }
        }

        public void Dispose()
        {
            httpClient?.Dispose();
        }
    }
}