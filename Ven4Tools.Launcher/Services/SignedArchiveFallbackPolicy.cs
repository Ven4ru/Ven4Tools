namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Установка клиента, когда подписанный version.json CDN не подтвердил SHA256
    /// архива (CDN недоступен или ещё не синхронизировался после релиза). Тогда
    /// единственный источник доверия — встроенная ECDSA-подпись архива, та же, что у
    /// «Установить из файла» (<see cref="LocalArchiveVerifier"/>). Подпись
    /// подтверждает содержимое и версию архива, но не то, что это та версия, которую
    /// пользователь ставит, и не то, что она не старее установленной: подписанный,
    /// но старый архив, выданный под видом нового, откатил бы клиента на версию с
    /// уже исправленными уязвимостями. Этих двух проверок и касается политика.
    ///
    /// Чистые функции: решение по версиям, без файлов и сети.
    /// </summary>
    internal static class SignedArchiveFallbackPolicy
    {
        /// <summary>
        /// Можно ли вообще пробовать установку по подписи архива — до скачивания.
        /// Даунгрейд отсекается сразу, не тратя минуты на загрузку.
        /// </summary>
        /// <returns>null — можно; иначе причина отказа.</returns>
        public static string? CheckBeforeDownload(string requestedVersion, string? installedVersion)
        {
            if (installedVersion != null && VersionComparer.Compare(requestedVersion, installedVersion) < 0)
                return $"версия {requestedVersion} старее установленной {installedVersion} — без подтверждения CDN откат не выполняется";
            return null;
        }

        /// <summary>
        /// Годится ли проверенный архив после скачивания.
        /// </summary>
        /// <param name="signedVersion">Версия из встроенной подписи (уже проверенной).</param>
        /// <param name="requestedVersion">Версия, которую ставят (из списка версий).</param>
        /// <param name="installedVersion">Версия на диске, если клиент установлен.</param>
        /// <returns>null — можно ставить; иначе причина отказа.</returns>
        public static string? CheckSignedArchive(string? signedVersion, string requestedVersion, string? installedVersion)
        {
            if (string.IsNullOrWhiteSpace(signedVersion))
                return "у архива нет встроенной подписи — без подтверждения CDN установить его нельзя";

            if (VersionComparer.Compare(signedVersion, requestedVersion) != 0)
                return $"подпись архива относится к версии {signedVersion}, а ставится {requestedVersion} — архив подменён";

            if (installedVersion != null && VersionComparer.Compare(signedVersion, installedVersion) < 0)
                return $"версия архива {signedVersion} старее установленной {installedVersion} — откат не выполняется";

            return null;
        }
    }
}
