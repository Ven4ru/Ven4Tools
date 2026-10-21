using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Состояние «Контролируемого доступа к папкам» Defender. Enabled = null — нет
    /// данных (Defender выключен сторонним антивирусом, WMI недоступен, запрос
    /// запрещён): в этом случае пользователя НЕ пугаем.
    /// </summary>
    internal sealed record ControlledFolderAccessStatus(
        bool? Enabled,
        IReadOnlyList<string> CustomProtectedFolders,
        IReadOnlyList<string> AllowedApplications)
    {
        public static ControlledFolderAccessStatus Unknown { get; } =
            new(null, Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>Чем рискованна папка установки клиента.</summary>
    internal sealed record InstallLocationAssessment(bool InOneDrive, bool UnderControlledFolderAccess)
    {
        public bool IsRisky => InOneDrive || UnderControlledFolderAccess;

        /// <summary>Объяснение для пользователя (пусто, если рисков нет).</summary>
        public string Describe()
        {
            var parts = new List<string>();
            if (InOneDrive)
                parts.Add("• Папка находится в OneDrive: сотни мегабайт программы будут синхронизироваться " +
                          "в облако, а файлы могут превратиться в «заглушки», которые скачиваются только при запуске.");
            if (UnderControlledFolderAccess)
                parts.Add("• Папка защищена «Контролируемым доступом к папкам» Защитника Windows: он запрещает " +
                          "запись неизвестным программам, и установка или обновление клиента могут молча не сработать.");
            return string.Join("\n", parts);
        }
    }

    /// <summary>
    /// Проверка папки установки клиента: OneDrive и «Контролируемый доступ к папкам».
    /// Только чтение — ничего в Windows не меняется, исключений в Defender не
    /// добавляется. Решения (рискованно ли) — чистые функции, покрыты unit-тестами;
    /// обращения к системе — отдельными методами.
    /// </summary>
    internal static class InstallLocationRisk
    {
        // FILE_ATTRIBUTE_RECALL_ON_OPEN / FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: файл или
        // папка — облачная заглушка (OneDrive «Файлы по запросу» и подобные).
        internal const int RecallOnOpen = 0x00040000;
        internal const int RecallOnDataAccess = 0x00400000;

        // EnableControlledFolderAccess: 0 — выкл, 1 — блокировать, 2 — только аудит,
        // 3/4 — защита только от изменения дисков (запись файлов не блокируется).
        internal const int CfaBlock = 1;

        /// <summary>
        /// Лежит ли путь в одной из папок (или совпадает с ней). Сравнение по полным
        /// путям без учёта регистра, с границей по разделителю: «C:\OneDriveX» не внутри
        /// «C:\OneDrive».
        /// </summary>
        internal static bool IsUnderAnyRoot(string path, IEnumerable<string?> roots)
        {
            string full;
            try { full = Normalize(path); }
            catch { return false; }

            foreach (string? root in roots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                string r;
                try { r = Normalize(root); }
                catch { continue; }
                if (string.Equals(full, r, StringComparison.OrdinalIgnoreCase) ||
                    full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Есть ли у атрибутов признак облачной заглушки.</summary>
        internal static bool IsCloudPlaceholder(int attributes) =>
            (attributes & (RecallOnOpen | RecallOnDataAccess)) != 0;

        /// <summary>
        /// Защищает ли CFA путь: режим «блокировать», путь в защищённой папке
        /// (стандартной или добавленной пользователем) и программа не в списке
        /// разрешённых. Нет данных о режиме — false.
        /// </summary>
        internal static bool IsProtectedByCfa(
            string path,
            ControlledFolderAccessStatus status,
            IEnumerable<string?> defaultProtectedFolders,
            string? launcherExePath)
        {
            if (status.Enabled != true) return false;

            if (!string.IsNullOrWhiteSpace(launcherExePath) &&
                status.AllowedApplications.Any(a => PathsEqual(a, launcherExePath)))
                return false;

            return IsUnderAnyRoot(path, defaultProtectedFolders.Concat(status.CustomProtectedFolders));
        }

        /// <summary>Корни OneDrive текущего пользователя из переменных окружения.</summary>
        internal static IReadOnlyList<string> OneDriveRoots() =>
            new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
                .Select(Environment.GetEnvironmentVariable)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>
        /// Папки, которые CFA защищает по умолчанию (личные и общие «Документы»,
        /// «Изображения», «Видео», «Музыка», «Рабочий стол», «Избранное»). Перенаправленная
        /// в OneDrive папка возвращается по её фактическому пути.
        /// </summary>
        internal static IReadOnlyList<string> DefaultCfaProtectedFolders()
        {
            var folders = new[]
            {
                Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.MyPictures,
                Environment.SpecialFolder.MyVideos,
                Environment.SpecialFolder.MyMusic,
                Environment.SpecialFolder.Desktop,
                Environment.SpecialFolder.Favorites,
                Environment.SpecialFolder.CommonDocuments,
                Environment.SpecialFolder.CommonPictures,
                Environment.SpecialFolder.CommonVideos,
                Environment.SpecialFolder.CommonMusic,
                Environment.SpecialFolder.CommonDesktopDirectory,
            };
            return folders
                .Select(f => { try { return Environment.GetFolderPath(f); } catch { return ""; } })
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
        }

        /// <summary>
        /// Лежит ли путь в облачной папке: по корням OneDrive или по атрибуту-заглушке
        /// у самого пути либо у ближайших существующих родителей (OneDrive не обязан
        /// быть в переменных окружения — например, у другого облачного клиента).
        /// </summary>
        internal static bool IsInOneDrive(string path)
        {
            if (IsUnderAnyRoot(path, OneDriveRoots())) return true;

            try
            {
                for (var dir = new DirectoryInfo(Path.GetFullPath(path)); dir != null; dir = dir.Parent)
                {
                    if (!dir.Exists) continue;
                    if (IsCloudPlaceholder((int)dir.Attributes)) return true;
                }
            }
            catch
            {
                // Нет доступа к атрибутам — нет данных, не пугаем.
            }
            return false;
        }

        private static Task<ControlledFolderAccessStatus>? _cfaStatus;
        private static readonly object CfaSync = new();

        /// <summary>
        /// Состояние CFA — один запрос WMI на процесс (результат кэшируется: CFA меняют
        /// редко, а запрос занимает до секунды). Начинается при первом обращении.
        /// </summary>
        internal static Task<ControlledFolderAccessStatus> GetCfaStatusAsync()
        {
            lock (CfaSync)
            {
                return _cfaStatus ??= Task.Run(QueryCfaStatus);
            }
        }

        /// <summary>
        /// Уже полученное состояние CFA без ожидания; ещё не готово — «нет данных».
        /// </summary>
        internal static ControlledFolderAccessStatus CfaStatusIfReady()
        {
            var task = GetCfaStatusAsync();
            return task.IsCompletedSuccessfully ? task.Result : ControlledFolderAccessStatus.Unknown;
        }

        /// <summary>Полная оценка папки (OneDrive + CFA).</summary>
        internal static InstallLocationAssessment Assess(string path, ControlledFolderAccessStatus cfa)
        {
            string? launcherExe = Environment.ProcessPath;
            return new InstallLocationAssessment(
                IsInOneDrive(path),
                IsProtectedByCfa(path, cfa, DefaultCfaProtectedFolders(), launcherExe));
        }

        // MSFT_MpPreference в root\Microsoft\Windows\Defender — только чтение, через
        // COM-объект WMI (WbemScripting.SWbemLocator): System.Management в .NET — это
        // отдельный пакет, а заводить его ради одного запроса незачем.
        private static ControlledFolderAccessStatus QueryCfaStatus()
        {
            try
            {
                Type? locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: false);
                if (locatorType == null) return ControlledFolderAccessStatus.Unknown;

                dynamic locator = Activator.CreateInstance(locatorType)!;
                dynamic services = locator.ConnectServer(".", @"root\Microsoft\Windows\Defender");
                dynamic results = services.ExecQuery(
                    "SELECT EnableControlledFolderAccess, ControlledFolderAccessProtectedFolders, " +
                    "ControlledFolderAccessAllowedApplications FROM MSFT_MpPreference");
                if ((int)results.Count == 0) return ControlledFolderAccessStatus.Unknown;

                dynamic preference = results.ItemIndex(0);
                object? mode = preference.Properties_.Item("EnableControlledFolderAccess").Value;
                if (mode == null) return ControlledFolderAccessStatus.Unknown;

                return new ControlledFolderAccessStatus(
                    Convert.ToInt32(mode) == CfaBlock,
                    ToStringList(preference.Properties_.Item("ControlledFolderAccessProtectedFolders").Value),
                    ToStringList(preference.Properties_.Item("ControlledFolderAccessAllowedApplications").Value));
            }
            catch (Exception ex)
            {
                LauncherLog.Write($"ℹ️ Состояние «Контролируемого доступа к папкам» не получено: {ex.GetType().Name}");
                return ControlledFolderAccessStatus.Unknown;
            }
        }

        private static IReadOnlyList<string> ToStringList(object? value)
        {
            if (value is string single) return new[] { single };
            if (value is not IEnumerable items) return Array.Empty<string>();
            return items.Cast<object?>()
                .Select(o => o?.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToList();
        }

        private static bool PathsEqual(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try { return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static string Normalize(string path) =>
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
