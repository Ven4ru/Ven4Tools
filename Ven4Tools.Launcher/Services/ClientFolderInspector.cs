using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Есть ли в папке клиента что-то постороннее. Установка клиента заменяет папку
    /// целиком (прежняя переносится в резервную копию и удаляется), «Удалить клиент»
    /// удаляет её рекурсивно, а «Перенести клиент» уносит всё содержимое. Для папки,
    /// которую создал сам лаунчер, это верно. Но «Найти клиент на диске» привязывает
    /// лаунчер к папке, где нашёлся Ven4Tools.exe, — например, к D:\Tools, где рядом
    /// лежат другие программы, и они пропали бы при первом же обновлении.
    ///
    /// Свои файлы верхнего уровня — те, что перечислены в Ven4Tools.deps.json (его
    /// .NET кладёт в каждую публикацию, и в нём все сборки и нативные файлы), плюс сам
    /// exe и его служебные json. Подпапки — языковые папки .NET, Data (кэш каталога)
    /// и lang (пакет перевода). Всё остальное считается чужим. Без deps.json (папка
    /// повреждена или это не клиент) своими считаются только Ven4Tools.* и файлы
    /// среды .NET по расширению, а чужие exe — чужими.
    /// </summary>
    internal sealed class ClientFolderInspector
    {
        private const string DepsFileName = "Ven4Tools.deps.json";

        private static readonly string[] OwnFixedFiles =
        {
            LauncherPaths.ClientExeName, DepsFileName, "Ven4Tools.runtimeconfig.json",
            "Ven4Tools.dll", "Ven4Tools.pdb", "createdump.exe",
        };

        // Расширения, которые без deps.json ещё можно считать своими: сборки и
        // настройки среды. Исполняемые файлы сюда не входят — чужой exe рядом с
        // клиентом и есть главный признак «папки с другими программами».
        private static readonly HashSet<string> FallbackOwnExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".dll", ".json", ".pdb", ".sig",
        };

        private static readonly HashSet<string> OwnDirectories = new(StringComparer.OrdinalIgnoreCase)
        {
            "Data", "lang", "runtimes", "Localization",
        };

        private readonly HashSet<string>? _listedFiles;

        private ClientFolderInspector(HashSet<string>? listedFiles) => _listedFiles = listedFiles;

        /// <summary>Читает deps.json папки (если есть) и готовит проверку.</summary>
        public static ClientFolderInspector For(string folder) =>
            new(ReadListedFiles(Path.Combine(folder, DepsFileName)));

        /// <summary>Проверка по готовому списку файлов из deps.json (для тестов).</summary>
        internal static ClientFolderInspector FromListedFiles(IEnumerable<string>? listed) =>
            new(listed == null ? null : new HashSet<string>(listed, StringComparer.OrdinalIgnoreCase));

        /// <summary>
        /// Посторонние элементы верхнего уровня папки (не больше <paramref name="limit"/>
        /// имён). Пусто — папка выглядит как чистая папка клиента или её нет.
        /// </summary>
        public static IReadOnlyList<string> FindForeignEntries(string folder, int limit = 5)
        {
            var foreign = new List<string>();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return foreign;

            var inspector = For(folder);
            foreach (string dir in Directory.EnumerateDirectories(folder))
            {
                string name = Path.GetFileName(dir);
                if (!IsOwnDirectoryName(name)) foreign.Add(name + Path.DirectorySeparatorChar);
                if (foreign.Count >= limit) return foreign;
            }

            foreach (string file in Directory.EnumerateFiles(folder))
            {
                string name = Path.GetFileName(file);
                if (!inspector.IsOwnFileName(name)) foreign.Add(name);
                if (foreign.Count >= limit) return foreign;
            }
            return foreign;
        }

        /// <summary>
        /// Удаляет из папки только то, что принадлежит клиенту (по тем же правилам,
        /// что <see cref="FindForeignEntries"/>); посторонние файлы и папки остаются.
        /// Ссылки (junction/symlink) не обходятся: удаляется сама ссылка, не её цель.
        /// </summary>
        public static void DeleteOwnEntries(string folder)
        {
            // Список своих файлов читается ДО удаления: deps.json сам среди них.
            var inspector = For(folder);
            foreach (string dir in Directory.EnumerateDirectories(folder).ToList())
            {
                if (!IsOwnDirectoryName(Path.GetFileName(dir))) continue;
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) Directory.Delete(dir);
                else Directory.Delete(dir, recursive: true);
            }
            foreach (string file in Directory.EnumerateFiles(folder).ToList())
            {
                if (inspector.IsOwnFileName(Path.GetFileName(file))) File.Delete(file);
            }
        }

        /// <summary>Файл верхнего уровня принадлежит клиенту. Покрыто unit-тестами.</summary>
        internal bool IsOwnFileName(string name)
        {
            if (OwnFixedFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
            // «файл.new-{id}» / «файл.old-{id}» прерванного блочного обновления — свои:
            // их разбирает зачистка при старте.
            if (TransactionalDirectoryInstaller.IsTransientArtifactName(name)) return true;
            if (_listedFiles != null) return _listedFiles.Contains(name);
            return FallbackOwnExtensions.Contains(Path.GetExtension(name));
        }

        /// <summary>Имя подпапки из публикации клиента. Покрыто unit-тестами.</summary>
        internal static bool IsOwnDirectoryName(string name)
        {
            if (OwnDirectories.Contains(name)) return true;
            // Папки сопутствующих сборок .NET называются по культуре: ru, de, pt-BR, zh-Hans…
            try
            {
                return CultureInfo.GetCultureInfo(name, predefinedOnly: true).Name.Length > 0;
            }
            catch (CultureNotFoundException)
            {
                return false;
            }
        }

        /// <summary>
        /// Имена файлов из разделов runtime/native/resources всех библиотек deps.json.
        /// null — файла нет или он не читается. Покрыто unit-тестами.
        /// </summary>
        internal static HashSet<string>? ParseListedFiles(string json)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (JObject.Parse(json)["targets"] is not JObject targets) return null;
            foreach (var target in targets.Properties())
            {
                if (target.Value is not JObject libraries) continue;
                foreach (var library in libraries.Properties())
                {
                    foreach (string section in new[] { "runtime", "native", "resources" })
                    {
                        if (library.Value[section] is not JObject assets) continue;
                        foreach (var asset in assets.Properties())
                            names.Add(Path.GetFileName(asset.Name));
                    }
                }
            }
            return names.Count > 0 ? names : null;
        }

        private static HashSet<string>? ReadListedFiles(string depsPath)
        {
            try
            {
                if (!File.Exists(depsPath)) return null;
                // Файл в несколько сотен КБ; больше — точно не наш deps.json.
                if (new FileInfo(depsPath).Length > 8 * 1024 * 1024) return null;
                return ParseListedFiles(File.ReadAllText(depsPath));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Перечисление для сообщения пользователю.</summary>
        public static string Describe(IReadOnlyList<string> entries) =>
            string.Join("\n", entries.Select(e => "• " + e));
    }
}
