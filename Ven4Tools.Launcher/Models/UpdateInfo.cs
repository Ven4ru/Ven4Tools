using System;
using System.Collections.Generic;

namespace Ven4Tools.Launcher.Models
{
    public class UpdateInfo
    {
        public bool HasUpdate { get; set; }
        public string? CurrentVersion { get; set; }      // ← ДОЛЖНО БЫТЬ
        public string? LatestVersion { get; set; }       // ← ДОЛЖНО БЫТЬ
        public string? DownloadUrl { get; set; }
        public string? ReleaseNotes { get; set; }
        public string? Error { get; set; }
        public long FileSize { get; set; }               // ← ДОЛЖНО БЫТЬ
        public bool IsCritical { get; set; }
        public bool IsInstalled { get; set; }
    }

    public class GitHubRelease
    {
        public string? tag_name { get; set; }
        public string? name { get; set; }
        public string? body { get; set; }
        public DateTime published_at { get; set; }
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
        public DateTime ReleaseDate { get; set; }
        public string? ReleaseNotes { get; set; }
        public bool IsLatest { get; set; }
        public bool IsInstalled { get; set; }
        public long FileSize { get; set; }
    }
}