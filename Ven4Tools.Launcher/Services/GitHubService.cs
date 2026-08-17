// Services/GitHubService.cs
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Net.Http;
using Ven4Tools.Launcher.Helpers;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Тонкий клиент GitHub API: HTTP-запросы, кэш списка релизов и отправка
    /// отчёта об ошибке через серверный прокси. Знания о том, какие ассеты
    /// относятся к клиенту или к установщику лаунчера, здесь нет — она вынесена
    /// в <see cref="ClientVersionMapper"/> и <see cref="LauncherUpdateSelector"/>,
    /// а очистка персональных данных — в
    /// <see cref="PersonalDataSanitizer"/> (её зовут и те места, где GitHub
    /// вообще не участвует).
    /// </summary>
    public class GitHubService
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
            return ClientVersionMapper.MapReleases(releases);
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
                return LauncherUpdateSelector.SelectLauncherUpdate(releases, currentVersion);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Получение последней стабильной версии winget с GitHub.
        /// Остаётся здесь, а не в отдельном классе: это такой же анонимный GET к
        /// api.github.com тем же <see cref="_sharedClient"/> с теми же заголовками —
        /// отличается только репозиторий (microsoft/winget-cli вместо нашего).
        /// Разбора ассетов и правил именования тут нет, выносить нечего.
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
                    title = PersonalDataSanitizer.Sanitize(title),
                    body  = PersonalDataSanitizer.Sanitize(body),
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
    }
}
