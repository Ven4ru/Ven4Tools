namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Почему для версии клиента нет подтверждённого SHA256 архива. Хеш берётся
    /// только из подписанного version.json CDN (fail-closed), а он публикует хеш
    /// одной — текущей — версии. Причин отсутствия несколько, и пользователю
    /// нужна именно его: «CDN недоступен» при работающем CDN, который просто не
    /// успел синхронизироваться после релиза, отправляло человека чинить сеть.
    ///
    /// Чистая функция: по состоянию, запомненному при загрузке списка версий.
    /// </summary>
    internal static class ClientHashAvailability
    {
        internal readonly record struct Explanation(string Reason, string Advice);

        /// <param name="version">Версия, которую ставят.</param>
        /// <param name="cdnManifestLoaded">Подписанный version.json получен и проверен.</param>
        /// <param name="cdnClientVersion">Версия клиента из этого манифеста (если есть).</param>
        public static Explanation Explain(string version, bool cdnManifestLoaded, string? cdnClientVersion)
        {
            if (!cdnManifestLoaded)
            {
                return new Explanation(
                    "CDN недоступен — подписанный манифест с хешем архива не получен",
                    "Проверьте подключение к интернету и повторите попытку позже.");
            }

            if (string.IsNullOrWhiteSpace(cdnClientVersion))
            {
                return new Explanation(
                    "подписанный манифест CDN получен, но сведений о клиенте в нём нет",
                    "Повторите попытку позже или обратитесь к автору проекта.");
            }

            int order = VersionComparer.Compare(version, cdnClientVersion);
            if (order > 0)
            {
                return new Explanation(
                    $"CDN доступен, но его подписанный манифест ещё не знает версию {version} " +
                    $"(в нём {cdnClientVersion}) — CDN не успел синхронизироваться после выхода релиза",
                    "Обычно синхронизация занимает несколько минут — повторите попытку позже.");
            }

            if (order == 0)
            {
                return new Explanation(
                    $"в подписанном манифесте CDN для версии {version} нет корректного SHA256",
                    "Повторите попытку позже или обратитесь к автору проекта.");
            }

            return new Explanation(
                $"подписанный манифест CDN подтверждает только текущую версию {cdnClientVersion}, " +
                $"для более старой {version} хеша нет",
                $"Установите версию {cdnClientVersion} или подписанный архив нужной версии через «Установить из файла».");
        }
    }
}
