using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Задание на установку набора без участия человека — из командной строки или
    /// из файла ответа.
    /// <para>
    /// <c>Ven4Tools.exe --install V4T:firefox,7zip --silent</c><br/>
    /// <c>Ven4Tools.exe --answer-file D:\набор.json</c><br/>
    /// <c>Ven4Tools.exe --update-apps --silent</c>
    /// </para>
    /// <para>
    /// Без <c>--silent</c> окно открывается как обычно, программы отмечаются и
    /// установка начинается сразу — с привычными вопросами. С <c>--silent</c> окно
    /// свёрнуто, вопросы не задаются (на каждый есть ответ в задании), по окончании
    /// клиент завершается с кодом возврата (см. <see cref="UnattendedExitCode"/>).
    /// </para>
    /// </summary>
    public sealed class UnattendedRequest
    {
        /// <summary>Идентификаторы приложений каталога в порядке, в котором их назвали.</summary>
        public IReadOnlyList<string> AppIds { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Задание «обновить установленные программы» (<c>--update-apps</c>) вместо
        /// установки набора. Список исключений берётся из настроек автообновления.
        /// </summary>
        public bool UpdateApps { get; init; }

        /// <summary>Не задавать вопросов и завершиться после установки.</summary>
        public bool Silent { get; init; }

        /// <summary>
        /// Создавать ли точку восстановления перед установкой. null — как решит
        /// обычный сценарий: с окном спросить, в тихом режиме не создавать.
        /// </summary>
        public bool? RestorePoint { get; init; }

        /// <summary>Диск установки («D:»); null — тот, что выбран в клиенте.</summary>
        public string? InstallDrive { get; init; }

        /// <summary>
        /// Можно ли поставить недостающий winget или Chocolatey. В тихом режиме спросить
        /// некого, поэтому ответ даётся заранее; по умолчанию — можно.
        /// </summary>
        public bool AllowPackageManagers { get; init; } = true;

        /// <summary>Куда записать итог в JSON; null — не записывать.</summary>
        public string? ReportPath { get; init; }

        /// <summary>
        /// Папка переносного офлайн-набора (в ней лежит каталог кэша установщиков).
        /// Задана — клиент работает офлайн на этот сеанс и ставит программы из неё.
        /// </summary>
        public string? OfflineCachePath { get; init; }

        /// <summary>
        /// Папка с драйверами набора «Перед переустановкой»: они возвращаются до
        /// установки программ. null — драйверов в задании нет.
        /// </summary>
        public string? RestoreDriversPath { get; init; }

        /// <summary>Папка с профилями Wi-Fi того же набора; null — их в задании нет.</summary>
        public string? RestoreWifiPath { get; init; }

        /// <summary>В задании есть что вернуть до установки программ.</summary>
        public bool HasRestore => RestoreDriversPath != null || RestoreWifiPath != null;
    }

    /// <summary>Коды возврата клиента в тихом режиме.</summary>
    public static class UnattendedExitCode
    {
        public const int Success = 0;
        /// <summary>Часть приложений не установилась.</summary>
        public const int PartialFailure = 1;
        /// <summary>Командная строка или файл ответа не разобраны.</summary>
        public const int InvalidRequest = 2;
        /// <summary>Каталог не загрузился либо ни одно приложение из задания в нём не найдено.</summary>
        public const int NothingToInstall = 3;
        /// <summary>Клиент уже запущен — второй экземпляр установку не начинает.</summary>
        public const int AlreadyRunning = 4;
    }

    public static class UnattendedCommandLine
    {
        public enum ParseStatus
        {
            /// <summary>Аргументов тихого режима нет — обычный запуск.</summary>
            None,
            Ok,
            Error
        }

        private const int MaxAnswerFileLength = 256 * 1024;

        /// <summary>
        /// Разбирает аргументы. Параметры командной строки сильнее одноимённых полей
        /// файла ответа: файл — заготовка, строка — уточнение к конкретному запуску.
        /// </summary>
        /// <param name="readFile">Чтение файла ответа; вынесено параметром ради тестов.</param>
        public static ParseStatus Parse(
            IReadOnlyList<string> args, Func<string, string> readFile,
            out UnattendedRequest? request, out string error)
        {
            request = null;
            error = "";

            string? install = null, answerFile = null, drive = null, report = null, offlineCache = null;
            bool silent = false, updateApps = false;
            bool? restorePoint = null;
            bool sawAny = false;

            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                string? Value()
                {
                    return i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                        ? args[++i]
                        : null;
                }

                switch (arg.ToLowerInvariant())
                {
                    case "--install":
                        sawAny = true;
                        install = Value();
                        if (install == null) { error = "После --install нужен код набора (V4T:…) или список идентификаторов через запятую."; return ParseStatus.Error; }
                        break;
                    case "--answer-file":
                        sawAny = true;
                        answerFile = Value();
                        if (answerFile == null) { error = "После --answer-file нужен путь к файлу ответа."; return ParseStatus.Error; }
                        break;
                    case "--drive":
                        drive = Value();
                        if (drive == null) { error = "После --drive нужна буква диска, например D:."; return ParseStatus.Error; }
                        break;
                    case "--report":
                        report = Value();
                        if (report == null) { error = "После --report нужен путь к файлу итога."; return ParseStatus.Error; }
                        break;
                    case "--offline-cache":
                        offlineCache = Value();
                        if (offlineCache == null) { error = "После --offline-cache нужна папка офлайн-набора."; return ParseStatus.Error; }
                        break;
                    case "--update-apps":
                        sawAny = true;
                        updateApps = true;
                        break;
                    case "--silent":
                        silent = true;
                        break;
                    case "--restore-point":
                        restorePoint = true;
                        break;
                    case "--no-restore-point":
                        restorePoint = false;
                        break;
                }
            }

            // --silent, --drive и прочие сами по себе ничего не значат: без набора
            // устанавливать нечего, и это обычный запуск клиента.
            if (!sawAny) return ParseStatus.None;

            AnswerFile? file = null;
            if (answerFile != null)
            {
                try
                {
                    string json = readFile(answerFile);
                    if (json.Length > MaxAnswerFileLength)
                    {
                        error = "Файл ответа слишком велик.";
                        return ParseStatus.Error;
                    }
                    file = JsonSerializer.Deserialize<AnswerFile>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    });
                }
                catch (Exception ex)
                {
                    error = $"Не удалось прочитать файл ответа: {ex.Message}";
                    return ParseStatus.Error;
                }
                if (file == null)
                {
                    error = "Файл ответа пуст.";
                    return ParseStatus.Error;
                }
            }

            var ids = new List<string>();
            if (!TryAddIds(ids, install, out error)) return ParseStatus.Error;
            if (file != null)
            {
                if (!TryAddIds(ids, file.Code, out error)) return ParseStatus.Error;
                if (file.Apps != null && !TryAddIds(ids, string.Join(",", file.Apps), out error)) return ParseStatus.Error;
            }

            ids = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (updateApps && ids.Count > 0)
            {
                error = "--update-apps и набор для установки задаются разными запусками.";
                return ParseStatus.Error;
            }
            // Драйверы и Wi-Fi берутся только из папки самого файла ответа: файл может
            // прийти откуда угодно, а установка драйверов идёт с правами администратора.
            if (!TryResolveInsideAnswerFolder(file?.Drivers, answerFile, out string? driversPath)
                || !TryResolveInsideAnswerFolder(file?.Wifi, answerFile, out string? wifiPath))
            {
                error = "Папки драйверов и Wi-Fi в файле ответа задаются относительно самого файла и не могут выходить за его папку.";
                return ParseStatus.Error;
            }
            bool hasRestore = driversPath != null || wifiPath != null;

            if (!updateApps && ids.Count == 0 && !hasRestore)
            {
                error = "В задании нет ни одного приложения.";
                return ParseStatus.Error;
            }
            if (updateApps && hasRestore)
            {
                error = "--update-apps и возврат драйверов задаются разными запусками.";
                return ParseStatus.Error;
            }

            string? resolvedDrive = drive ?? file?.Drive;
            if (resolvedDrive != null && !TryNormalizeDrive(resolvedDrive, out resolvedDrive))
            {
                error = "Диск установки задаётся буквой, например D:.";
                return ParseStatus.Error;
            }

            request = new UnattendedRequest
            {
                AppIds = ids,
                UpdateApps = updateApps,
                Silent = silent || file?.Silent == true,
                RestorePoint = restorePoint ?? file?.RestorePoint,
                InstallDrive = resolvedDrive,
                AllowPackageManagers = file?.AllowPackageManagers ?? true,
                ReportPath = report ?? file?.Report,
                OfflineCachePath = offlineCache ?? ResolveBesideAnswerFile(file?.OfflineCache, answerFile),
                RestoreDriversPath = driversPath,
                RestoreWifiPath = wifiPath
            };
            return ParseStatus.Ok;
        }

        /// <summary>
        /// Относительный путь из файла ответа → полный, если он остаётся внутри папки
        /// файла. Пустое значение — «не задано» (успех, путь null).
        /// </summary>
        private static bool TryResolveInsideAnswerFolder(string? path, string? answerFile, out string? resolved)
        {
            resolved = null;
            if (string.IsNullOrWhiteSpace(path)) return true;
            if (answerFile == null || System.IO.Path.IsPathRooted(path)) return false;

            string? directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(answerFile));
            if (directory == null) return false;

            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, path));
            string root = directory.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;

            resolved = full;
            return true;
        }

        /// <summary>
        /// Принимает и код набора («V4T:a,b», ссылку с сайта), и голый список через
        /// запятую. Разбор и отсев недопустимых идентификаторов — тот же, что у кнопки
        /// «Набор с сайта».
        /// </summary>
        private static bool TryAddIds(List<string> target, string? raw, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(raw)) return true;

            string text = raw.Trim();
            bool looksLikeCode = text.StartsWith(SitePresetService.CodePrefix, StringComparison.OrdinalIgnoreCase)
                || text.Contains("ven4tools.ru", StringComparison.OrdinalIgnoreCase);
            var parsed = SitePresetService.Parse(looksLikeCode ? text : SitePresetService.CodePrefix + text);
            if (!parsed.Success)
            {
                error = parsed.Error;
                return false;
            }

            target.AddRange(parsed.AppIds);
            return true;
        }

        /// <summary>
        /// Путь из файла ответа отсчитывается от папки самого файла: «.» — рядом с ним.
        /// Так набор на флешке работает с любой буквой диска.
        /// </summary>
        private static string? ResolveBesideAnswerFile(string? path, string? answerFile)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (System.IO.Path.IsPathRooted(path) || answerFile == null) return path;
            string? directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(answerFile));
            return directory == null ? path : System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, path));
        }

        private static bool TryNormalizeDrive(string value, out string? drive)
        {
            drive = null;
            string text = value.Trim().TrimEnd('\\', '/');
            if (text.Length == 1) text += ":";
            if (text.Length != 2 || text[1] != ':' || !char.IsAsciiLetter(text[0])) return false;
            drive = char.ToUpperInvariant(text[0]) + ":";
            return true;
        }

        private sealed class AnswerFile
        {
            public List<string>? Apps { get; set; }
            public string? Code { get; set; }
            public bool? Silent { get; set; }
            public bool? RestorePoint { get; set; }
            public string? Drive { get; set; }
            public bool? AllowPackageManagers { get; set; }
            public string? Report { get; set; }
            public string? OfflineCache { get; set; }
            public string? Drivers { get; set; }
            public string? Wifi { get; set; }
        }
    }

    /// <summary>Итог тихой установки — то, что записывается в файл по <c>--report</c>.</summary>
    public sealed class UnattendedReport
    {
        public int ExitCode { get; set; }
        public string Message { get; set; } = "";
        public List<string> Installed { get; set; } = new();
        public List<UnattendedFailure> Failed { get; set; } = new();
        /// <summary>Идентификаторы из задания, которых нет в каталоге.</summary>
        public List<string> NotFound { get; set; } = new();
        /// <summary>Есть в каталоге, но сейчас недоступны для установки.</summary>
        public List<string> Unavailable { get; set; } = new();
        /// <summary>Установлены, но для завершения нужна перезагрузка.</summary>
        public List<string> RebootRequired { get; set; } = new();
        /// <summary>Что возвращено из набора «Перед переустановкой» до установки программ.</summary>
        public List<string> Restored { get; set; } = new();
        public string StartedUtc { get; set; } = "";
        public string FinishedUtc { get; set; } = "";
    }

    public sealed class UnattendedFailure
    {
        public string Id { get; set; } = "";
        public string Reason { get; set; } = "";
    }
}
