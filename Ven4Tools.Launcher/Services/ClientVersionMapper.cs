// Services/ClientVersionMapper.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Отображение релизов GitHub в версии клиента Ven4Tools: какие ассеты считаются
    /// клиентским архивом, какой релиз считается «latest» и как релиз превращается
    /// в <see cref="ClientVersionInfo"/>.
    ///
    /// Отделено от <see cref="GitHubService"/>: тот отвечает только за запрос к API
    /// (HTTP, кэш, коды ошибок), а знание о правилах именования наших ассетов — это
    /// доменная логика релизов Ven4Tools, к транспорту отношения не имеющая.
    /// Все методы — чистые функции без сети, их же зовёт ручной список версий
    /// (MainWindow.LoadVersionsAsync).
    /// </summary>
    internal static class ClientVersionMapper
    {
        /// <summary>
        /// Клиентский zip-ассет релиза: имя содержит «Client» или «Ven4Tools»,
        /// оканчивается на «.zip» и не относится к лаунчеру. Единый предикат для
        /// автообновления и MainWindow.LoadVersionsAsync (ручной список версий) —
        /// раньше он дублировался в обоих местах и разошёлся.
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
        /// Полный список версий клиента из набора релизов, отсортированный от новой
        /// к старой. Релизы без клиентского архива отбрасываются.
        /// </summary>
        internal static List<ClientVersionInfo> MapReleases(List<GitHubRelease> releases)
        {
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
    }
}
