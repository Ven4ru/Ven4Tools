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
        // Единый HttpClient на весь процесс: пересоздание экземпляра в каждом
        // GitHubService приводит к утечке сокетов (socket exhaustion). Заголовки
        // и таймаут задаются один раз и не меняются между запросами.
        private static readonly HttpClient _sharedClient = CreateSharedClient();
        private readonly string repoOwner;
        private readonly string repoName;

        private static HttpClient CreateSharedClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools.Launcher");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
            return client;
        }

        // Отдельный статический HttpClient для отправки краш-отчётов через прокси
        // ven4tools.ru: без заголовка Authorization/Accept и с увеличенным таймаутом.
        // Пересоздание экземпляра на каждый вызов CreateIssueAsync приводило бы к
        // утечке сокетов (socket exhaustion); прокси-URL фиксирован на весь процесс.
        private static readonly HttpClient _proxyClient = CreateProxyClient();

        private static HttpClient CreateProxyClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools.Launcher");
            return client;
        }

        // Кэш списка релизов: CheckLauncherUpdate, GetAvailableClientVersions и
        // OfferInstallationAsync дёргают один и тот же endpoint. Без кэша каждый
        // вызов — отдельный запрос к GitHub API (лимит 60/час с IP). Живёт 5 минут.
        private static readonly object _releasesCacheLock = new();
        private static (List<GitHubRelease>? data, DateTime ts) _releasesCache;
        private static readonly TimeSpan _releasesCacheTtl = TimeSpan.FromMinutes(5);

        public GitHubService() : this("Ven4ru", "Ven4Tools")
        {
        }

        public GitHubService(string repoOwner, string repoName)
        {
            this.repoOwner = repoOwner;
            this.repoName = repoName;

            // Листинг релизов публичного репозитория не требует авторизации:
            // лимит 60 запросов/час с IP лаунчеру хватает с запасом, а токен
            // в распространяемом exe был бы доступен для извлечения.
            // HttpClient — статический singleton (см. _sharedClient).
        }

        /// <summary>
        /// Получение всех релизов
        /// </summary>
        public async Task<(List<GitHubRelease> Releases, string? Error)> GetAllReleasesWithError()
        {
            // Свежий кэш — отдаём без запроса к API.
            lock (_releasesCacheLock)
            {
                if (_releasesCache.data != null &&
                    DateTime.UtcNow - _releasesCache.ts < _releasesCacheTtl)
                    return (_releasesCache.data, null);
            }

            try
            {
                // ?per_page=100 — без пагинации GitHub отдаёт лишь первые 30 релизов,
                // и самообновление сломается, как только релизов станет больше 30.
                string url = $"https://api.github.com/repos/{repoOwner}/{repoName}/releases?per_page=100";
                using var response = await _sharedClient.GetAsync(url);

                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    return (new(), $"GitHub rate limit (403) — подождите ~1 час");
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return (new(), $"Репозиторий не найден (404)");
                if (!response.IsSuccessStatusCode)
                    return (new(), $"GitHub вернул {(int)response.StatusCode}");

                string json = await response.Content.ReadAsStringAsync();
                var list = JsonSerializer.Deserialize<List<GitHubRelease>>(json) ?? new();

                lock (_releasesCacheLock)
                    _releasesCache = (list, DateTime.UtcNow);

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
        /// Клиентский zip-ассет релиза: имя содержит «Client» или «Ven4Tools»,
        /// оканчивается на «.zip» и не относится к лаунчеру. Единый предикат для
        /// GetAvailableClientVersions (автообновление) и MainWindow.LoadVersionsAsync
        /// (ручной список версий) — раньше он дублировался в обоих местах и разошёлся.
        /// </summary>
        internal static bool IsClientZipAsset(GitHubAsset? asset)
        {
            return asset?.name != null &&
                   (asset.name.Contains("Client", StringComparison.OrdinalIgnoreCase) ||
                    asset.name.Contains("Ven4Tools", StringComparison.OrdinalIgnoreCase)) &&
                   asset.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                   !asset.name.Contains("Launcher", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Первый стабильный релиз с клиентским zip-архивом («latest»):
        /// launcher-only релизы (без zip) не должны помечаться как latest.
        /// </summary>
        internal static GitHubRelease? FindFirstStableClientRelease(List<GitHubRelease> releases) =>
            releases.FirstOrDefault(r => !r.prerelease && r.assets?.Any(IsClientZipAsset) == true);

        /// <summary>Клиентский zip-ассет данного релиза (или null, если его нет).</summary>
        internal static GitHubAsset? FindClientZipAsset(GitHubRelease release) =>
            release.assets?.FirstOrDefault(IsClientZipAsset);

        /// <summary>
        /// Базовое отображение релиза в ClientVersionInfo с GitHub-ссылкой.
        /// Возвращает null, если у релиза нет тега или клиентского zip-ассета.
        /// CDN-подстановка (ZipUrl/FallbackUrl/ExpectedSha256) и проверка
        /// доверенности хоста применяются поверх, в MainWindow.LoadVersionsAsync.
        /// </summary>
        internal static ClientVersionInfo? MapRelease(GitHubRelease release, GitHubRelease? firstStable)
        {
            var version = release.tag_name?.TrimStart('v');
            if (string.IsNullOrEmpty(version)) return null;

            var clientAsset = FindClientZipAsset(release);
            if (clientAsset == null) return null;

            return new ClientVersionInfo
            {
                Version      = version,
                DownloadUrl  = clientAsset.browser_download_url ?? "",
                ReleaseDate  = release.published_at,
                ReleaseNotes = release.body,
                IsLatest     = release == firstStable,
                FileSize     = clientAsset.size
            };
        }

        /// <summary>
        /// Получение списка доступных версий клиента.
        /// Используется автообновлением (UpdateBackgroundService.CheckClientAsync)
        /// только для обнаружения новой версии и текста уведомления — фактическая
        /// загрузка идёт через MainWindow.LoadVersionsAsync с проверкой хоста, CDN
        /// и SHA256 (см. TriggerAutoClientUpdateAsync), поэтому здесь эти шаги
        /// намеренно не повторяются: DownloadUrl этого списка для скачивания не берётся.
        /// </summary>
        public async Task<List<ClientVersionInfo>> GetAvailableClientVersions()
        {
            var releases = await GetAllReleases();
            var firstStable = FindFirstStableClientRelease(releases);

            var versions = new List<ClientVersionInfo>();
            foreach (var release in releases)
            {
                var info = MapRelease(release, firstStable);
                if (info != null) versions.Add(info);
            }

            versions.Sort((a, b) => VersionComparer.Compare(b.Version, a.Version));
            return versions;
        }

        /// <summary>
        /// Проверка, есть ли обновление лаунчера.
        /// Сканирует все релизы — GetLatestRelease() не подходит: при раздельных тегах
        /// (launcher-vX.Y.Z и vX.Y.Z) «latest» может быть клиентским релизом без установщика.
        /// </summary>
        public async Task<UpdateInfo?> CheckLauncherUpdate(string currentVersion)
        {
            try
            {
                var (releases, _) = await GetAllReleasesWithError();
                if (releases.Count == 0) return null;
                return SelectLauncherUpdate(releases, currentVersion);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Ассет установщика лаунчера в релизе: Ven4Tools.Setup-X.Y.Z.exe.
        /// Самообновление и установка идут только через установщик — отдельный
        /// «голый» exe лаунчера в релизах больше не публикуется и не ищется.
        /// </summary>
        internal static bool IsLauncherSetupAsset(string? name)
        {
            return name != null &&
                   name.StartsWith("Ven4Tools.Setup", StringComparison.OrdinalIgnoreCase) &&
                   name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Выбор новейшего стабильного релиза лаунчера с установщиком.
        /// Чистая функция без сети — покрыта unit-тестами.
        /// </summary>
        internal static UpdateInfo? SelectLauncherUpdate(List<GitHubRelease> releases, string currentVersion)
        {
            string? latestVersion = null;
            string? downloadUrl = null;
            long fileSize = 0;
            string? releaseNotes = null;

            foreach (var release in releases)
            {
                if (release.prerelease || release.tag_name == null) continue;

                var asset = release.assets?.FirstOrDefault(a => IsLauncherSetupAsset(a.name));
                if (asset == null) continue;

                string ver = ParseVersionFromTag(release.tag_name);
                if (latestVersion == null || VersionComparer.IsNewer(ver, latestVersion))
                {
                    latestVersion = ver;
                    downloadUrl = asset.browser_download_url;
                    fileSize = asset.size;
                    releaseNotes = release.body;
                }
            }

            if (latestVersion == null) return null;

            return new UpdateInfo
            {
                HasUpdate = VersionComparer.IsNewer(latestVersion, currentVersion),
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                DownloadUrl = downloadUrl,
                ReleaseNotes = releaseNotes,
                FileSize = fileSize
            };
        }

        // "launcher-v2.0.0" → "2.0.0", "v3.4.2" → "3.4.2"
        internal static string ParseVersionFromTag(string tag)
        {
            string v = tag.TrimStart('v');
            if (v.StartsWith("launcher-", StringComparison.OrdinalIgnoreCase))
                v = v["launcher-".Length..].TrimStart('v');
            return v;
        }

        /// <summary>
        /// Получение последней стабильной версии winget с GitHub
        /// </summary>
        public async Task<string?> GetLatestWingetVersionAsync()
        {
            try
            {
                string url = "https://api.github.com/repos/microsoft/winget-cli/releases/latest";
                using var response = await _sharedClient.GetAsync(url);

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

        // Серверный прокси для отправки отчётов об ошибках.
        // GitHub-токен для создания issue хранится на сервере (config.php), а не в exe,
        // поэтому его нельзя извлечь реверс-инжинирингом распространяемого лаунчера.
        private const string CrashProxyUrl = "https://ven4tools.ru/api/db.php?action=report_crash";

        /// <summary>
        /// Удаление персональных данных из текста перед отправкой в публичный репозиторий:
        /// имя пользователя, имя машины и пути вида C:\Users\имя\ заменяются плейсхолдерами.
        /// </summary>
        public static string SanitizePersonalData(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";

            // Пути профилей: C:\Users\имя\ → C:\Users\<пользователь>\
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"([A-Za-z]:\\Users\\)[^\\\r\n]+",
                "$1<пользователь>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Тот же путь с forward-slash и UNC-путь без буквы диска — тот же класс,
            // что и в клиентском CrashReportService.SanitizePath (кросс-модульная
            // согласованность).
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"([A-Za-z]:/Users/)[^/\r\n]+",
                "$1<пользователь>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"(\\\\[^\\\r\n]+\\Users\\)[^\\\r\n]+",
                "$1<пользователь>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Имя пользователя и имя машины в произвольных местах текста.
            // Короткие значения (< 3 символов) не заменяем — слишком много ложных срабатываний.
            string user = Environment.UserName;
            if (!string.IsNullOrEmpty(user) && user.Length >= 3)
                text = text.Replace(user, "<пользователь>", StringComparison.OrdinalIgnoreCase);

            string machine = Environment.MachineName;
            if (!string.IsNullOrEmpty(machine) && machine.Length >= 3)
                text = text.Replace(machine, "<машина>", StringComparison.OrdinalIgnoreCase);

            return text;
        }

        /// <summary>
        /// Короткий хэш идентификатора сессии: достаточен для дедупликации отчётов,
        /// но не раскрывает исходный SessionId в публичном репозитории.
        /// </summary>
        public static string HashSessionId(string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return "";
            byte[] hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(sessionId));
            return Convert.ToHexString(hash)[..8].ToLowerInvariant();
        }

        /// <summary>
        /// Отправка отчёта об ошибке через серверный прокси ven4tools.ru.
        /// Сервер сам создаёт issue в репозитории, используя свой токен.
        /// </summary>
        public async Task<(bool Success, string? IssueUrl, string? Error)> CreateIssueAsync(
            string title, string body, string[]? labels = null)
        {
            try
            {
                // Защита от утечки PII: убираем имя пользователя, машины и пути профиля
                // из любых данных, уходящих в публичный репозиторий
                var payload = new
                {
                    title = SanitizePersonalData(title),
                    body  = SanitizePersonalData(body),
                    labels = labels ?? new[] { "bug" }
                };

                // Статический HttpClient без заголовка Authorization — токен GitHub
                // на сервер передавать не нужно (и нельзя). См. _proxyClient.
                var content = new System.Net.Http.StringContent(
                    System.Text.Json.JsonSerializer.Serialize(payload),
                    System.Text.Encoding.UTF8,
                    "application/json");

                using var response = await _proxyClient.PostAsync(CrashProxyUrl, content);
                string json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return (false, null, $"Сервер вернул {(int)response.StatusCode}: {json}");

                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var errProp))
                    return (false, null, errProp.GetString());

                string? issueUrl =
                    root.TryGetProperty("issue_url", out var iu) ? iu.GetString() :
                    root.TryGetProperty("html_url", out var hu) ? hu.GetString() : null;

                return (true, issueUrl, null);
            }
            catch (Exception ex)
            {
                return (false, null, ex.Message);
            }
        }

        public void Dispose()
        {
            // HttpClient — статический singleton, разделяется между всеми
            // экземплярами и живёт весь процесс. Здесь его не освобождаем.
        }
    }
}