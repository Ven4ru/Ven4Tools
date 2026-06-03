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
            if (!string.IsNullOrEmpty(Secrets.GitHubToken))
                httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {Secrets.GitHubToken}");
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
        public async Task<(List<GitHubRelease> Releases, string? Error)> GetAllReleasesWithError()
        {
            try
            {
                string url = $"https://api.github.com/repos/{repoOwner}/{repoName}/releases";
                using var response = await httpClient.GetAsync(url);

                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    return (new(), $"GitHub rate limit (403) — подождите ~1 час или добавьте токен");
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return (new(), $"Репозиторий не найден (404)");
                if (!response.IsSuccessStatusCode)
                    return (new(), $"GitHub вернул {(int)response.StatusCode}");

                string json = await response.Content.ReadAsStringAsync();
                var list = JsonSerializer.Deserialize<List<GitHubRelease>>(json) ?? new();
                return (list, null);
            }
            catch (Exception ex)
            {
                return (new(), $"Сетевая ошибка: {ex.Message}");
            }
        }

        public async Task<List<GitHubRelease>> GetAllReleases()
        {
            var (releases, _) = await GetAllReleasesWithError();
            return releases;
        }

        /// <summary>
        /// Получение списка доступных версий клиента
        /// </summary>
        public async Task<List<ClientVersionInfo>> GetAvailableClientVersions()
        {
            var versions = new List<ClientVersionInfo>();
            var releases = await GetAllReleases();

            var firstStable = releases.FirstOrDefault(r => !r.prerelease);
            foreach (var release in releases)
            {
                var version = release.tag_name?.TrimStart('v');
                if (string.IsNullOrEmpty(version)) continue;

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
                        Version      = version,
                        DownloadUrl  = clientAsset.browser_download_url ?? "",
                        ReleaseDate  = release.published_at,
                        ReleaseNotes = release.body,
                        IsPreRelease = release.prerelease,
                        IsLatest     = release == firstStable,
                        FileSize     = clientAsset.size
                    });
                }
            }

            versions.Sort((a, b) => CompareVersions(b.Version, a.Version));
            return versions;
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
            a.name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

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
        private static int CompareVersions(string v1, string v2)
        {
            var parts1 = v1.Split('.');
            var parts2 = v2.Split('.');
            for (int i = 0; i < Math.Max(parts1.Length, parts2.Length); i++)
            {
                // Отрезаем суффикс вроде "-pre", "-rc1" перед парсингом числа
                string s1 = i < parts1.Length ? parts1[i].Split('-')[0] : "0";
                string s2 = i < parts2.Length ? parts2[i].Split('-')[0] : "0";
                int num1 = int.TryParse(s1, out var x) ? x : 0;
                int num2 = int.TryParse(s2, out var y) ? y : 0;
                if (num1 != num2) return num1.CompareTo(num2);
            }
            // При равных числах стабильная версия выше pre-release ("3.1.0" > "3.1.0-pre")
            bool v1IsPre = v1.Contains('-');
            bool v2IsPre = v2.Contains('-');
            if (v1IsPre != v2IsPre) return v1IsPre ? -1 : 1;
            return 0;
        }

        public async Task<(bool Success, string? IssueUrl, string? Error)> CreateIssueAsync(
            string title, string body, string[]? labels = null)
        {
            try
            {
                string url = $"https://api.github.com/repos/{repoOwner}/{repoName}/issues";
                var payload = new
                {
                    title,
                    body,
                    labels = labels ?? new[] { "bug" }
                };
                var content = new System.Net.Http.StringContent(
                    System.Text.Json.JsonSerializer.Serialize(payload),
                    System.Text.Encoding.UTF8,
                    "application/json");

                using var response = await httpClient.PostAsync(url, content);
                string json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return (false, null, $"GitHub API {(int)response.StatusCode}: {json}");

                using var doc = System.Text.Json.JsonDocument.Parse(json);
                string? issueUrl = doc.RootElement
                    .TryGetProperty("html_url", out var u) ? u.GetString() : null;

                return (true, issueUrl, null);
            }
            catch (Exception ex)
            {
                return (false, null, ex.Message);
            }
        }

        public void Dispose()
        {
            httpClient?.Dispose();
        }
    }
}