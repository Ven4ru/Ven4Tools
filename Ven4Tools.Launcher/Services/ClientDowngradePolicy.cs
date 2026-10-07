namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Как «Установить из файла» и <c>--install-from</c> поступают с архивом более
    /// старой версии, чем установленная или опубликованная.
    /// </summary>
    internal enum LocalArchiveDowngradeMode
    {
        /// <summary>Спросить пользователя (окно лаунчера), по умолчанию — «Нет».</summary>
        Ask,

        /// <summary>Отказать без вопросов (командная строка без ключа).</summary>
        Refuse,

        /// <summary>Установить, записав предупреждение в журнал (<c>--allow-downgrade</c>).</summary>
        Allow,
    }

    /// <summary>
    /// Понижение версии клиента: архив подписан и подлинный, но старее того, что уже
    /// стоит на диске, или того, что сейчас опубликовано. Подпись подтверждает
    /// содержимое и версию архива, но не его свежесть — старая сборка остаётся
    /// правильно подписанной навсегда, вместе со всеми исправленными после неё
    /// ошибками. Публикуется при этом только текущая сборка, так что старый архив
    /// может прийти лишь со стороны: из чужих рук, из давней загрузки, из скрипта.
    ///
    /// Сетевой путь от такого закрыт сам (обновление предлагается только на более
    /// новую версию, см. также <see cref="SignedArchiveFallbackPolicy"/>); здесь — то
    /// же решение для локального архива и общая проверка «старее установленной».
    ///
    /// Чистые функции: решение по версиям, без файлов и сети.
    /// </summary>
    internal static class ClientDowngradePolicy
    {
        /// <summary>
        /// Чем установка является понижением. Заполнены только те версии, которых
        /// архив действительно старее.
        /// </summary>
        internal readonly record struct Finding(
            string ArchiveVersion, string? InstalledVersion, string? PublishedVersion)
        {
            /// <summary>Одна фраза для журнала, вопроса пользователю и отказа.</summary>
            public string Describe()
            {
                if (InstalledVersion != null && PublishedVersion != null)
                {
                    return VersionComparer.Compare(InstalledVersion, PublishedVersion) == 0
                        ? $"Это более старая версия {ArchiveVersion}, сейчас установлена и опубликована {PublishedVersion}."
                        : $"Это более старая версия {ArchiveVersion}: сейчас установлена {InstalledVersion}, а опубликована {PublishedVersion}.";
                }

                return InstalledVersion != null
                    ? $"Это более старая версия {ArchiveVersion}, сейчас установлена {InstalledVersion}."
                    : $"Это более старая версия {ArchiveVersion}, сейчас опубликована {PublishedVersion}.";
            }
        }

        /// <summary>Ключ командной строки, разрешающий понижение версии.</summary>
        public const string AllowDowngradeSwitch = "--allow-downgrade";

        private const string NoFixesNote = "Старые сборки не получают исправлений.";

        /// <summary>
        /// Старее ли версия той, что установлена. Версия на диске читается из
        /// метаданных exe («6.0.2.0») и предрелизной метки не несёт, поэтому у
        /// кандидата метка здесь тоже не учитывается: иначе повторная установка
        /// «6.1.0-beta» поверх неё же считалась бы понижением.
        /// </summary>
        public static bool IsOlderThanInstalled(string? candidateVersion, string? installedVersion)
        {
            if (string.IsNullOrWhiteSpace(candidateVersion) || string.IsNullOrWhiteSpace(installedVersion))
                return false;

            string candidate = candidateVersion.Trim();
            int dash = candidate.IndexOf('-');
            if (dash >= 0) candidate = candidate[..dash];
            return VersionComparer.Compare(candidate, installedVersion) < 0;
        }

        /// <summary>
        /// Является ли установка архива понижением версии.
        /// </summary>
        /// <param name="archiveVersion">Версия архива — из уже проверенной подписи.</param>
        /// <param name="installedVersion">Версия на диске, если клиент установлен.</param>
        /// <param name="publishedVersion">Текущая опубликованная версия, если известна.</param>
        /// <returns>
        /// null — не понижение. Неизвестная версия архива понижением не считается:
        /// сравнивать нечего, а назвать архив «более старой версией» было бы неправдой.
        /// </returns>
        public static Finding? Check(string? archiveVersion, string? installedVersion, string? publishedVersion)
        {
            if (string.IsNullOrWhiteSpace(archiveVersion)) return null;

            bool olderThanInstalled = IsOlderThanInstalled(archiveVersion, installedVersion);
            bool olderThanPublished = !string.IsNullOrWhiteSpace(publishedVersion) &&
                                      VersionComparer.Compare(archiveVersion, publishedVersion) < 0;
            if (!olderThanInstalled && !olderThanPublished) return null;

            return new Finding(
                archiveVersion,
                olderThanInstalled ? installedVersion : null,
                olderThanPublished ? publishedVersion : null);
        }

        /// <summary>
        /// Самая новая из известных версий (пустые пропускаются); null — не известна
        /// ни одна. Опубликованную версию лаунчер узнаёт из нескольких мест и в разное
        /// время — сравнивать архив нужно с наибольшей из них.
        /// </summary>
        public static string? Newest(params string?[] versions)
        {
            string? newest = null;
            foreach (string? version in versions)
            {
                if (string.IsNullOrWhiteSpace(version)) continue;
                if (newest == null || VersionComparer.IsNewer(version, newest)) newest = version;
            }
            return newest;
        }

        /// <summary>Вопрос пользователю в окне лаунчера.</summary>
        public static string BuildQuestion(Finding finding) =>
            $"{finding.Describe()} {NoFixesNote}\n\nВсё равно установить?";

        /// <summary>Отказ для командной строки — с ключом, который его снимает.</summary>
        public static string BuildRefusal(Finding finding) =>
            $"{finding.Describe()} {NoFixesNote} Установка отменена; " +
            $"чтобы всё равно установить этот архив, добавьте ключ {AllowDowngradeSwitch}.";
    }
}
