using System;
using System.Collections.Generic;

namespace Ven4Tools.Models
{
    public enum UpdatePriority
    {
        Minor = 0,
        Recommended = 1,
        Critical = 2
    }

    public class UpdateInfo
    {
        public bool HasUpdate { get; set; }
        public string? LatestVersion { get; set; }
        public string? CurrentVersion { get; set; }
        public string? DownloadUrl { get; set; }
        public string? ReleaseNotes { get; set; }
        public string? Error { get; set; }
        public long FileSize { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public UpdatePriority Priority { get; set; }

        public bool IsCritical => Priority == UpdatePriority.Critical;
        public bool IsRecommended => Priority == UpdatePriority.Recommended;
        public bool IsMinor => Priority == UpdatePriority.Minor;

        public string PriorityDisplay => Priority switch
        {
            UpdatePriority.Minor => "🔹 Минорное",
            UpdatePriority.Recommended => "🔸 Рекомендуемое",
            UpdatePriority.Critical => "🔴 Критическое",
            _ => "📦 Обновление"
        };
    }

    public class GitHubRelease
    {
        public string tag_name { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public string body { get; set; } = string.Empty;
        public DateTime published_at { get; set; }
        public bool prerelease { get; set; }
        public List<GitHubAsset> assets { get; set; } = new();
    }

    public class GitHubAsset
    {
        public string name { get; set; } = string.Empty;
        public string browser_download_url { get; set; } = string.Empty;
        public long size { get; set; }
        public string? content_type { get; set; }
        public DateTime created_at { get; set; }
    }
}