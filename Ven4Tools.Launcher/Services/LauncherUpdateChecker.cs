// Services/LauncherUpdateChecker.cs
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Обнаружение обновления лаунчера: есть ли версия новее текущей и откуда её
    /// брать. Только решение — ничего не скачивает и не запускает.
    ///
    /// Порядок источников: подписанный version.json CDN (основной — работает, даже
    /// если GitHub заблокирован по SNI), затем GitHub Releases как резерв.
    /// Собственного HttpClient и предпочтения источника загрузки здесь нет: сеть
    /// ведут <see cref="CdnService"/> и <see cref="GitHubService"/>, у каждого свой
    /// клиент. Именно поэтому проверка и установка разнесены по разным классам —
    /// общего состояния у них нет, зависимость односторонняя
    /// (<see cref="LauncherUpdateInstaller"/> зовёт проверку, но не наоборот).
    /// </summary>
    internal sealed class LauncherUpdateChecker
    {
        private readonly Action<string>? _log;

        internal LauncherUpdateChecker(Action<string>? log = null)
        {
            _log = log;
        }

        private void Log(string message)
        {
            _log?.Invoke(message);
            Debug.WriteLine(message);
        }

        /// <summary>
        /// Проверка обновления лаунчера для версии текущей сборки.
        /// Возвращает null при сетевой ошибке (для вызывающего кода это «обновлений нет»).
        /// </summary>
        internal Task<UpdateInfo?> CheckForUpdateAsync() =>
            CheckForUpdateAsync(LauncherInstallation.GetCurrentVersion());

        /// <summary>
        /// То же, но с явной текущей версией (для фоновой проверки, где версия
        /// передаётся снаружи). "0.0.0" — «любая доступная версия считается новее».
        /// </summary>
        internal async Task<UpdateInfo?> CheckForUpdateAsync(string currentVersion)
        {
            try
            {
                return await ResolveSetupUpdateAsync(currentVersion);
            }
            catch (Exception ex)
            {
                Log($"Ошибка проверки обновлений лаунчера: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Обнаружение обновления установщика: сначала CDN version.json (основной
        /// источник — работает, даже если GitHub заблокирован по SNI), затем GitHub
        /// Releases как резерв. GitHub-обнаружение обогащается CDN-ссылками и SHA256
        /// (для той же версии), чтобы дальнейшая загрузка шла по полной цепочке
        /// источников (CDN/зеркало/GitHub), а не только через GitHub.
        ///
        /// Если CDN уже показал обновление — возвращаем его сразу, не сверяясь с
        /// GitHub на предмет ещё более новой версии (в отличие от клиентской проверки
        /// в UpdateBackgroundService.CheckClientAsync, которая всегда берёт max по
        /// обоим источникам). Допустимо, т.к. релиз лаунчера деплоится на CDN тем же
        /// действием, что публикует GitHub-релиз — CDN не может показывать версию
        /// СТАРЕЕ реально доступной на GitHub в штатном сценарии. Если это когда-либо
        /// перестанет быть так — привести к той же max-логике, что у клиента.
        ///
        /// Внутренний метод: установка (<see cref="LauncherUpdateInstaller"/>) зовёт
        /// его напрямую с "0.0.0", чтобы найти самую свежую доступную версию.
        /// </summary>
        internal async Task<UpdateInfo?> ResolveSetupUpdateAsync(string currentVersion)
        {
            // 1. CDN version.json — основной источник обнаружения версии лаунчера.
            var cdnUpdate = await TryCheckViaCdnAsync(currentVersion);
            if (cdnUpdate != null) return cdnUpdate;

            // 2. GitHub Releases — резерв (или CDN не показал обновления / лаг CDN,
            //    когда релиз уже на GitHub, но ещё не задеплоен на CDN).
            var gitHub = new GitHubService();
            var info = await gitHub.CheckLauncherUpdate(currentVersion);
            if (info == null)
            {
                Log("Не удалось получить информацию о релизах (CDN и GitHub недоступны).");
                return null;
            }

            if (info.HasUpdate && string.IsNullOrEmpty(info.DownloadUrl))
            {
                // Релиз новее, но установщика в нём нет — обновлять нечем.
                Log($"В релизе {info.LatestVersion} нет установщика Ven4Tools.Setup — обновление пропущено.");
                info.HasUpdate = false;
            }

            if (info.HasUpdate && !string.IsNullOrEmpty(info.LatestVersion))
            {
                await EnrichWithCdnAsync(info);
            }

            return info;
        }

        /// <summary>
        /// Обнаружение обновления через подписанный version.json CDN. Возвращает
        /// UpdateInfo с обновлением ТОЛЬКО если CDN доступен, подписан, содержит
        /// валидный SHA256 и версию новее текущей. Иначе — null (проверит GitHub).
        /// </summary>
        private static async Task<UpdateInfo?> TryCheckViaCdnAsync(string currentVersion)
        {
            using var cdn = new CdnService();
            CdnVersionInfo? cdnInfo = await cdn.GetVersionInfoAsync();
            var l = cdnInfo?.Launcher;
            if (l == null || string.IsNullOrEmpty(l.Version) ||
                !DownloadValidator.IsValidSha256(l.SetupSha256))
                return null;

            if (!VersionComparer.IsNewer(l.Version, currentVersion))
                return null; // CDN не показывает обновления — пусть решает GitHub (мог обогнать CDN)

            return new UpdateInfo
            {
                HasUpdate = true,
                CurrentVersion = currentVersion,
                LatestVersion = l.Version,
                // Для обратной совместимости DownloadUrl держим GitHub-ссылку (если есть),
                // иначе CDN-ссылку — фактическую загрузку ведёт цепочка BuildSetupCandidates.
                DownloadUrl = l.SetupFallback ?? l.SetupUrl,
                SetupCdnUrl = l.SetupUrl,
                SetupMirrorHostingUrl = l.SetupMirrorHosting,
                SetupGithubUrl = l.SetupFallback,
                ExpectedSha256 = l.SetupSha256
            };
        }

        /// <summary>
        /// Дополняет GitHub-обнаруженное обновление ссылками CDN/зеркала и SHA256 из
        /// подписанного version.json — только если версия на CDN совпадает с найденной
        /// (иначе хеш относится к другому билду). Без подтверждённого хеша дальнейшая
        /// загрузка откажет (fail-closed).
        /// </summary>
        private static async Task EnrichWithCdnAsync(UpdateInfo info)
        {
            try
            {
                using var cdn = new CdnService();
                CdnVersionInfo? cdnInfo = await cdn.GetVersionInfoAsync();
                var l = cdnInfo?.Launcher;
                if (l != null &&
                    string.Equals(l.Version, info.LatestVersion, StringComparison.OrdinalIgnoreCase) &&
                    DownloadValidator.IsValidSha256(l.SetupSha256))
                {
                    info.ExpectedSha256 = l.SetupSha256;
                    info.SetupCdnUrl = l.SetupUrl;
                    info.SetupMirrorHostingUrl = l.SetupMirrorHosting;
                    info.SetupGithubUrl = l.SetupFallback ?? info.DownloadUrl;
                }
            }
            catch
            {
                // CDN недоступен — хеш не подтверждён, скачивание будет отменено (fail-closed).
            }
        }
    }
}
