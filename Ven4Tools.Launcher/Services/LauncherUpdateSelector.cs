// Services/LauncherUpdateSelector.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Выбор обновления лаунчера из набора релизов GitHub: какой ассет считается
    /// установщиком, как из тега получить версию и какой релиз победил.
    ///
    /// Отделено от <see cref="GitHubService"/>: тот знает только про HTTP и кэш,
    /// а правила именования тегов (launcher-vX.Y.Z рядом с клиентскими vX.Y.Z) и
    /// ассетов Ven4Tools.Setup-*.exe — доменное знание о наших релизах.
    /// Все методы — чистые функции без сети, покрыты unit-тестами.
    /// </summary>
    internal static class LauncherUpdateSelector
    {
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
                ReleaseNotes = releaseNotes
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
    }
}
