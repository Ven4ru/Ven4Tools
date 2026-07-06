using System;
using System.Collections.Generic;

namespace Ven4Tools.Launcher.Models
{
    public class UpdateInfo
    {
        public bool HasUpdate { get; set; }
        public string? CurrentVersion { get; set; }
        public string? LatestVersion { get; set; }
        public string? DownloadUrl { get; set; }
        public string? ReleaseNotes { get; set; }
        public long FileSize { get; set; }
    }

    public class GitHubRelease
    {
        public string? tag_name { get; set; }
        public string? name { get; set; }
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
        // Резервная ссылка (GitHub), когда основная DownloadUrl указывает на CDN.
        public string? FallbackUrl { get; set; }
        // Ожидаемый SHA256 zip-архива (из CDN). Если задан — проверяется после скачивания.
        public string? ExpectedSha256 { get; set; }
        public DateTime ReleaseDate { get; set; }
        public string? ReleaseNotes { get; set; }
        public bool IsLatest { get; set; }
        public bool IsPreRelease { get; set; }
        public bool IsInstalled { get; set; }
        public long FileSize { get; set; }
    }
}