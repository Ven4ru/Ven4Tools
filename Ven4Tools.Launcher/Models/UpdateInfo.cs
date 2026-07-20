using System;
using System.Collections.Generic;

namespace Ven4Tools.Launcher.Models
{
    public class UpdateInfo
    {
        public bool HasUpdate { get; set; }
        public string? CurrentVersion { get; set; }
        public string? LatestVersion { get; set; }
        // Основная ссылка на установщик (GitHub при обнаружении через GitHub, либо
        // GitHub-резерв при обнаружении через CDN) — сохранена для обратной совместимости.
        public string? DownloadUrl { get; set; }
        public string? ReleaseNotes { get; set; }

        // Ссылки-кандидаты на установщик из подписанного version.json CDN (когда
        // обновление обнаружено/обогащено через CDN). FallbackDownloader.BuildCandidates
        // разворачивает их в упорядоченную цепочку транспортов (CDN-домен, CDN-IP,
        // хостинг-зеркало, GitHub). SHA256 обязателен для скачивания (fail-closed).
        public string? SetupCdnUrl { get; set; }
        public string? SetupMirrorHostingUrl { get; set; }
        public string? SetupGithubUrl { get; set; }
        public string? ExpectedSha256 { get; set; }
    }

    public class GitHubRelease
    {
        public string? tag_name { get; set; }
        public string? body { get; set; }
        public DateTime published_at { get; set; }
        public bool prerelease { get; set; }
        public List<GitHubAsset>? assets { get; set; }
    }

    public class GitHubAsset
    {
        public string? name { get; set; }
        public string? browser_download_url { get; set; }
        public long size { get; set; }
    }

    public class ClientVersionInfo
    {
        public string Version { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        // Ожидаемый SHA256 zip-архива (из CDN). Если задан — проверяется после скачивания.
        public string? ExpectedSha256 { get; set; }
        // Ссылки по источникам для построения цепочки кандидатов (BuildCandidates):
        // CDN-домен, зеркало на хостинге, GitHub. Заполняются в LoadVersionsAsync, когда
        // CDN знает эту версию; иначе остаётся только GithubUrl (одиночный источник).
        public string? CdnUrl { get; set; }
        public string? MirrorHostingUrl { get; set; }
        public string? GithubUrl { get; set; }
        public DateTime ReleaseDate { get; set; }
        public string? ReleaseNotes { get; set; }
        public bool IsLatest { get; set; }
        public long FileSize { get; set; }
    }
}