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
    /// в <see cref="ClientVersionInfo"/>. Здесь же — чем запись дополняется из
    /// подписанного version.json CDN и как она строится из него одного, когда
    /// списка релизов нет.
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
        /// CDN-подстановка (<see cref="ApplyCdnClientInfo"/>) и проверка
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

        /// <summary>
        /// Дополняет запись версии данными блока client из ПОДПИСАННОГО version.json:
        /// ссылка на архив на CDN и на зеркале, SHA256 архива, адреса блочного
        /// (дельта-) обновления. Единственное место, где поля манифеста CDN попадают в
        /// <see cref="ClientVersionInfo"/> — им пользуются и список версий из релизов
        /// GitHub (подстановка поверх записи релиза), и запись, построенная целиком из
        /// манифеста (<see cref="BuildFromCdnManifest"/>).
        ///
        /// Ничего не меняет и возвращает false, если манифест описывает другую версию
        /// (его хеш относится к другому архиву) либо ссылка на архив пуста или ведёт
        /// на недоверенный хост.
        /// </summary>
        internal static bool ApplyCdnClientInfo(ClientVersionInfo info, CdnClientInfo? cdnClient)
        {
            if (cdnClient == null ||
                !string.Equals(cdnClient.Version, info.Version, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(cdnClient.ZipUrl) ||
                !DownloadValidator.IsAllowedDownloadHost(cdnClient.ZipUrl))
            {
                return false;
            }

            info.DownloadUrl = cdnClient.ZipUrl!;
            info.CdnUrl = cdnClient.ZipUrl;
            info.MirrorHostingUrl = cdnClient.ZipMirrorHosting;
            // Хеш из version.json относится к одному и тому же zip
            // (CDN, зеркало и GitHub отдают идентичный архив), поэтому
            // годится для всех источников. Это не свойство кода, а
            // условие выпуска: в релиз, на CDN и на зеркало кладётся
            // один и тот же файл. Если на GitHub окажется отдельно
            // собранный архив (два `dotnet publish` не воспроизводимы
            // побайтово), GitHub-кандидат будет молча отбраковываться
            // проверкой целостности — см. защиту от перезаписи ассета
            // в .github/workflows/release.yml.
            info.ExpectedSha256 = cdnClient.ZipSha256;

            // Ссылки блочного (дельта-) обновления. Опциональны: релиз
            // мог быть выпущен без файлового манифеста — тогда поля
            // останутся пустыми и обновление пойдёт полным путём.
            info.ManifestUrl = cdnClient.ManifestUrl;
            info.ManifestSignatureUrl = cdnClient.ManifestSignatureUrl;
            info.FilesBaseUrl = cdnClient.FilesBaseUrl;
            info.FilesBaseMirrorHostingUrl = cdnClient.FilesBaseMirrorHosting;
            return true;
        }

        /// <summary>
        /// Запись о текущей версии клиента, построенная целиком из подписанного
        /// version.json — для случая, когда списка релизов GitHub нет (api.github.com
        /// недоступен, исчерпан лимит запросов), а CDN отвечает. Манифест публикует
        /// ровно одну — текущую — версию клиента, её и описывает запись.
        ///
        /// На вход идёт только манифест с уже проверенной подписью: другого
        /// <see cref="CdnVersionInfo"/> не бывает, его создаёт
        /// <see cref="CdnService.ParseVerified"/>; null (CDN не ответил или подпись не
        /// подтверждена) даёт null. Запись строится только вместе с корректным SHA256
        /// архива: на этом пути нет записи релиза с GitHub, и хеш из подписанного
        /// манифеста — единственное, чем проверяется скачанное (fail-closed, как у
        /// обновления самого лаунчера в LauncherUpdateChecker).
        ///
        /// Даты и описания релиза в манифесте нет — эти поля остаются пустыми.
        /// </summary>
        internal static ClientVersionInfo? BuildFromCdnManifest(CdnVersionInfo? cdnInfo)
        {
            var client = cdnInfo?.Client;
            if (client == null || string.IsNullOrWhiteSpace(client.Version)) return null;
            if (!DownloadValidator.IsValidSha256(client.ZipSha256)) return null;

            var info = new ClientVersionInfo
            {
                Version = client.Version,
                // «latest» — только стабильная версия, как и в списке из релизов
                // GitHub (FindFirstStableClientRelease): предварительная сборка сама
                // к установке и обновлению не предлагается.
                IsLatest = !client.Version.Contains('-')
            };
            if (!ApplyCdnClientInfo(info, client)) return null;

            // Ссылка на тот же архив в релизе GitHub — крайний резерв в цепочке
            // источников. Недоверенный хост отбрасывается здесь же, без записи.
            if (DownloadValidator.IsAllowedDownloadHost(client.ZipFallback))
                info.GithubUrl = client.ZipFallback;

            return info;
        }
    }
}
