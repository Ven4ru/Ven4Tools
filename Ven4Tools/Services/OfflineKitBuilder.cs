using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Переносной офлайн-набор — «на флешку».
    ///
    /// Офлайн-кэш уже умеет хранить проверенные установщики в выбранной папке, в том
    /// числе на флешке. Набор добавляет к нему то, чего не хватает, чтобы поставить
    /// эти программы на другом компьютере без интернета и без заранее установленного
    /// Ven4Tools: копию самого клиента, файл ответа со списком программ и файл запуска.
    /// На новом компьютере достаточно открыть <c>install-set.cmd</c>.
    ///
    /// Клиент с флешки запускается в офлайн-режиме только на этот сеанс: настройки на
    /// новом компьютере он не меняет.
    /// </summary>
    public static class OfflineKitBuilder
    {
        public const string AnswerFileName = "ven4tools-set.json";
        public const string LauncherFileName = "install-set.cmd";
        public const string ReadmeFileName = "ПРОЧТИ.txt";
        public const string ClientFolderName = "Ven4Tools";

        public sealed record Result(int Apps, int ClientFiles, long ClientBytes);

        /// <param name="kitRoot">Папка набора — та же, где лежит каталог офлайн-кэша.</param>
        /// <param name="cachedAppIds">Программы, чьи установщики уже лежат в кэше.</param>
        /// <param name="clientDirectory">Папка работающего клиента — она копируется в набор.</param>
        public static Result Build(
            string kitRoot, IReadOnlyList<string> cachedAppIds, string clientDirectory,
            IProgress<string>? progress = null, CancellationToken ct = default)
        {
            if (cachedAppIds.Count == 0)
                throw new InvalidOperationException("В офлайн-кэше нет ни одного установщика — набор собирать не из чего.");

            Directory.CreateDirectory(kitRoot);

            progress?.Report("Копирование клиента…");
            var (files, bytes) = CopyClient(clientDirectory, Path.Combine(kitRoot, ClientFolderName), kitRoot, ct);

            progress?.Report("Запись файла ответа…");
            File.WriteAllText(Path.Combine(kitRoot, AnswerFileName), BuildAnswerFile(cachedAppIds), new UTF8Encoding(false));
            // Только ASCII: cmd.exe читает пакетный файл в OEM-кодировке, и кириллица
            // в путях превратилась бы в мусор.
            File.WriteAllText(Path.Combine(kitRoot, LauncherFileName), LauncherScript, Encoding.ASCII);
            File.WriteAllText(Path.Combine(kitRoot, ReadmeFileName), BuildReadme(cachedAppIds.Count), new UTF8Encoding(true));

            return new Result(cachedAppIds.Count, files, bytes);
        }

        /// <summary>
        /// Файл ответа набора. <c>offlineCache: "."</c> — кэш лежит рядом с файлом, поэтому
        /// набор работает с любой буквой диска. Тихий режим выключен: установку с флешки
        /// запускает человек, и ему нужно видеть ход и итог.
        /// </summary>
        public static string BuildAnswerFile(IReadOnlyList<string> appIds) =>
            JsonSerializer.Serialize(
                new { apps = appIds, offlineCache = ".", silent = false, restorePoint = true, allowPackageManagers = false },
                new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        public const string LauncherScript =
            "@echo off\r\n" +
            "rem Ven4Tools offline set: installs the programs stored next to this file.\r\n" +
            "start \"\" \"%~dp0" + ClientFolderName + "\\Ven4Tools.exe\" --answer-file \"%~dp0" + AnswerFileName + "\"\r\n";

        private static string BuildReadme(int apps) =>
            "Офлайн-набор Ven4Tools\r\n" +
            "======================\r\n\r\n" +
            $"Программ в наборе: {apps}.\r\n\r\n" +
            "Как поставить на другом компьютере:\r\n" +
            $"  1. Откройте {LauncherFileName} с этой флешки.\r\n" +
            "  2. Разрешите запуск от имени администратора.\r\n" +
            "  3. Ven4Tools отметит программы набора и начнёт установку.\r\n\r\n" +
            "Интернет не нужен: установщики лежат в папке Ven4ToolsCache и проверяются\r\n" +
            "по контрольной сумме из каталога перед запуском. Клиент работает прямо с\r\n" +
            "флешки и настройки на компьютере не меняет.\r\n";

        // Копия клиента без того, что к программе не относится: набор может лежать
        // внутри папки клиента или рядом, а кэш и журналы тащить с собой незачем.
        private static (int Files, long Bytes) CopyClient(string source, string target, string kitRoot, CancellationToken ct)
        {
            string sourceFull = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
            string targetFull = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
            string kitFull = Path.GetFullPath(kitRoot).TrimEnd(Path.DirectorySeparatorChar);

            if (!File.Exists(Path.Combine(sourceFull, "Ven4Tools.exe")))
                throw new FileNotFoundException("В папке клиента нет Ven4Tools.exe — копировать нечего.", sourceFull);
            if (IsSameOrInside(targetFull, sourceFull) || IsSameOrInside(sourceFull, targetFull))
                throw new InvalidOperationException("Папка набора не может находиться внутри папки клиента или совпадать с ней.");

            int files = 0;
            long bytes = 0;
            foreach (string file in Directory.EnumerateFiles(sourceFull, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                // Набор, собираемый внутри папки клиента, не копирует сам себя.
                if (IsSameOrInside(Path.GetFullPath(file), kitFull)) continue;

                string relative = Path.GetRelativePath(sourceFull, file);
                if (IsTransient(relative)) continue;

                string destination = Path.Combine(targetFull, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
                files++;
                bytes += new FileInfo(file).Length;
            }
            return (files, bytes);
        }

        private static bool IsSameOrInside(string path, string root) =>
            path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        // Папка Data копируется намеренно: в ней кэш каталога, по контрольным суммам
        // которого скачаны установщики набора. Встроенный в клиент каталог может быть
        // старше, и тогда на новом компьютере установщики не прошли бы проверку.
        private static readonly string[] TransientFolders = { "Logs", "Ven4ToolsCache" };

        private static bool IsTransient(string relativePath)
        {
            string first = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            return TransientFolders.Contains(first, StringComparer.OrdinalIgnoreCase)
                   || relativePath.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                   || relativePath.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase);
        }
    }
}
