// Services/GitHubService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services
{
    public class GitHubService : IDisposable
    {
        private readonly HttpClient httpClient;
        private readonly string repoOwner;
        private readonly string repoName;

        public GitHubService() : this("Ven4ru", "Ven4Tools")
        {
        }

        public GitHubService(string repoOwner, string repoName)
        {
            this.repoOwner = repoOwner;
            this.repoName = repoName;

            httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools.Launcher");
            httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
            httpClient.Timeout = TimeSpan.FromSeconds(15);
        }

        /// <summary>
        /// Получение последнего релиза
        /// </summary>
        public async Task<GitHubRelease?> GetLatestRelease()
        {
            try
            {
                string url = $"https://api.github.com/repos/{repoOwner}/{repoName}/releases/latest";
                using var response = await httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                    return null;

                string json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<GitHubRelease>(json);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Получение всех релизов
        /// </summary>
        public async Task<List<GitHubRelease>> GetAllReleases()
        {
            try
            {
                string url = $"https://api.github.com/repos/{repoOwner}/{repoName}/releases";
                using var response = await httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                    return new List<GitHubRelease>();

                string json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<List<GitHubRelease>>(json) ?? new List<GitHubRelease>();
            }
            catch
            {
                return new List<GitHubRelease>();
            }
        }

        /// <summary>
        /// Получение списка доступных версий клиента
        /// </summary>
        public async Task<List<ClientVersionInfo>> GetAvailableClientVersions()
        {
            var versions = new List<ClientVersionInfo>();
            var releases = await GetAllReleases();

            foreach (var release in releases)
            {
                var version = release.tag_name?.TrimStart('v');
                if (string.IsNullOrEmpty(version)) continue;

                // Ищем asset клиента (может быть назван по-разному)
                var clientAsset = release.assets?.FirstOrDefault(a =>
                    a.name != null &&
                    (a.name.Contains("Client", StringComparison.OrdinalIgnoreCase) ||
                     a.name.Contains("Ven4Tools", StringComparison.OrdinalIgnoreCase)) &&
                    a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    !a.name.Contains("Launcher", StringComparison.OrdinalIgnoreCase));

                if (clientAsset != null)
                {
                    versions.Add(new ClientVersionInfo
                    {
                        Version = version,
                        DownloadUrl = clientAsset.browser_download_url ?? "",
                        ReleaseDate = release.published_at,
                        ReleaseNotes = release.body,
                        IsLatest = releases.First() == release,
                        FileSize = clientAsset.size
                    });
                }
            }

            return versions.OrderByDescending(v => v.Version).ToList();
        }

        /// <summary>
        /// Проверка, есть ли обновление лаунчера
        /// </summary>
/// <summary>
/// Проверка, есть ли обновление лаунчера
/// </summary>
public async Task<UpdateInfo?> CheckLauncherUpdate(string currentVersion)
{
    try
    {
        var latest = await GetLatestRelease();
        if (latest?.tag_name == null) return null;

        string latestVersion = latest.tag_name.TrimStart('v');
        bool hasUpdate = CompareVersions(latestVersion, currentVersion) > 0;

        // Ищем asset лаунчера
        var launcherAsset = latest.assets?.FirstOrDefault(a =>
            a.name != null &&
            a.name.Contains("Launcher", StringComparison.OrdinalIgnoreCase) &&
            a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        return new UpdateInfo
        {
            HasUpdate = hasUpdate,
            CurrentVersion = currentVersion,
            LatestVersion = latestVersion,
            DownloadUrl = launcherAsset?.browser_download_url,
            ReleaseNotes = latest.body,
            FileSize = launcherAsset?.size ?? 0
        };
    }
    catch
    {
        return null;
    }
}
/// <summary>
/// Получение последней стабильной версии winget с GitHub
/// </summary>
public async Task<string?> GetLatestWingetVersionAsync()
{
    try
    {
        string url = "https://api.github.com/repos/microsoft/winget-cli/releases/latest";
        using var response = await httpClient.GetAsync(url);
        
        if (!response.IsSuccessStatusCode)
            return null;
        
        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        
        if (root.TryGetProperty("tag_name", out var tagProp))
        {
            string tag = tagProp.GetString() ?? "";
            return tag.TrimStart('v');
        }
        
        return null;
    }
    catch
    {
        return null;
    }
}
        /// <summary>
        /// Сравнение версий
        /// </summary>
        private int CompareVersions(string v1, string v2)
        {
            var parts1 = v1.Split('.');
            var parts2 = v2.Split('.');
            for (int i = 0; i < Math.Max(parts1.Length, parts2.Length); i++)
            {
                int num1 = i < parts1.Length ? int.Parse(parts1[i]) : 0;
                int num2 = i < parts2.Length ? int.Parse(parts2[i]) : 0;
                if (num1 != num2) return num1.CompareTo(num2);
            }
            return 0;
        }

        public void Dispose()
        {
            httpClient?.Dispose();
        }
    }
}