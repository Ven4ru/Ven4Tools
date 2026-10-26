using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Launcher.Helpers;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Манифест переноса: относительный путь → размер и SHA256 каждого файла папки
    /// клиента, снятый ДО копирования. Новая папка принимается только если совпадает
    /// с ним целиком — ни одного лишнего, недостающего или отличающегося файла.
    /// </summary>
    internal sealed class ClientFolderManifest
    {
        internal readonly record struct Entry(long Size, string Sha256);

        private readonly IReadOnlyDictionary<string, Entry> _files;

        internal ClientFolderManifest(IReadOnlyDictionary<string, Entry> files) => _files = files;

        public int FileCount => _files.Count;

        public long TotalBytes => _files.Values.Sum(e => e.Size);

        internal IReadOnlyDictionary<string, Entry> Files => _files;

        /// <summary>
        /// Снимает манифест каталога (рекурсивно). Ссылки (junction/symlink) внутри
        /// папки клиента не ожидаются — встреченная ссылка прерывает перенос: копировать
        /// «сквозь» неё значило бы унести чужие файлы, а пропустить — потерять свои.
        /// </summary>
        public static async Task<ClientFolderManifest> CaptureAsync(string directory, CancellationToken token)
        {
            string root = Path.GetFullPath(directory);
            var files = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in EnumerateFilesNoLinks(root))
            {
                token.ThrowIfCancellationRequested();
                long size = new FileInfo(file).Length;
                string hash = await FileHashHelper.ComputeSha256Async(file, token).ConfigureAwait(false);
                files[ToRelative(root, file)] = new Entry(size, hash);
            }
            return new ClientFolderManifest(files);
        }

        /// <summary>
        /// Сверка каталога с манифестом: null — совпал, иначе описание первого расхождения.
        /// </summary>
        public async Task<string?> FindDifferenceAsync(string directory, CancellationToken token)
        {
            string root = Path.GetFullPath(directory);
            if (!Directory.Exists(root)) return "папка не найдена";

            var actual = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in EnumerateFilesNoLinks(root))
            {
                token.ThrowIfCancellationRequested();
                string relative = ToRelative(root, file);
                // Лишний файл виден сразу — хешировать его незачем.
                if (!_files.ContainsKey(relative)) return $"лишний файл: {relative}";
                long size = new FileInfo(file).Length;
                string hash = await FileHashHelper.ComputeSha256Async(file, token).ConfigureAwait(false);
                actual[relative] = new Entry(size, hash);
            }
            return Compare(_files, actual);
        }

        /// <summary>
        /// Сравнение двух манифестов. Чистая функция — покрыта unit-тестами.
        /// </summary>
        internal static string? Compare(
            IReadOnlyDictionary<string, Entry> expected,
            IReadOnlyDictionary<string, Entry> actual)
        {
            foreach (var (path, entry) in actual)
            {
                if (!expected.TryGetValue(path, out var want)) return $"лишний файл: {path}";
                if (want.Size != entry.Size ||
                    !string.Equals(want.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    return $"файл отличается: {path}";
            }
            foreach (string path in expected.Keys)
            {
                if (!actual.ContainsKey(path)) return $"нет файла: {path}";
            }
            return null;
        }

        internal static IEnumerable<string> EnumerateFilesNoLinks(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    if (FileHelper.IsReparsePoint(sub))
                        throw new IOException($"В папке клиента есть ссылка на другую папку: {sub}");
                    pending.Push(sub);
                }
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    if (FileHelper.IsReparsePoint(file))
                        throw new IOException($"В папке клиента есть файл-ссылка: {file}");
                    yield return file;
                }
            }
        }

        private static string ToRelative(string root, string file) =>
            Path.GetRelativePath(root, file).Replace('\\', '/');
    }

    /// <summary>
    /// Копирование папки клиента и проверки путей для «Перенести клиент». Сама
    /// последовательность (журнал, смена указателя, удаление старой) — в
    /// MainWindow.Download.Move.cs; здесь только то, что не зависит от окна.
    /// </summary>
    internal static class ClientFolderMover
    {
        /// <summary>
        /// Можно ли переносить из <paramref name="sourceClientPath"/> в
        /// <paramref name="targetClientPath"/>. null — можно, иначе причина отказа.
        /// Чистая функция (кроме проверки существования целевой папки) — покрыта тестами.
        /// </summary>
        internal static string? ValidateTarget(string sourceClientPath, string targetClientPath, string dataFolderPath)
        {
            string source = Normalize(sourceClientPath);
            string target = Normalize(targetClientPath);

            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                return "клиент уже находится в этой папке";
            if (IsSubPath(target, source))
                return "новая папка находится внутри текущей папки клиента";
            if (IsSubPath(source, target))
                return "текущая папка клиента находится внутри новой";
            if (!InstallPathGuard.IsClientPathSafe(target, dataFolderPath))
                return "новая папка совпадает с системной или пересекается с папкой данных Ven4Tools";

            // В непустую папку не переносим: её содержимое пришлось бы либо смешать с
            // клиентом, либо удалить — а оно может быть чужим.
            try
            {
                if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                    return "в новой папке уже есть файлы";
            }
            catch (Exception ex)
            {
                return $"новая папка недоступна ({ex.Message})";
            }
            return null;
        }

        /// <summary>Имя временной папки копирования рядом с целевой — по шаблону
        /// остатков установки, чтобы зачистка при старте узнавала её.</summary>
        internal static string StagingPathFor(string targetClientPath)
        {
            string target = Normalize(targetClientPath);
            string parent = Path.GetDirectoryName(target)
                ?? throw new InvalidOperationException("Не удалось определить родительскую папку.");
            return Path.Combine(parent, $".{Path.GetFileName(target)}.staging-{Guid.NewGuid():N}");
        }

        /// <summary>
        /// Копирует папку целиком (структуру и файлы). progress получает число
        /// скопированных байт. Существующие файлы в назначении не перезаписываются —
        /// назначение обязано быть новой пустой папкой.
        /// </summary>
        internal static async Task CopyTreeAsync(
            string sourceDirectory, string destinationDirectory, Action<long>? progress, CancellationToken token)
        {
            string source = Path.GetFullPath(sourceDirectory);
            string destination = Path.GetFullPath(destinationDirectory);
            Directory.CreateDirectory(destination);

            long copied = 0;
            var buffer = new byte[1 << 20];
            foreach (string file in ClientFolderManifest.EnumerateFilesNoLinks(source))
            {
                token.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(source, file);
                string target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        copied += read;
                        progress?.Invoke(copied);
                    }
                    // Данные на диске до смены указателя: обрыв питания сразу после
                    // переключения не должен оставить клиента с недописанными файлами.
                    output.Flush(flushToDisk: true);
                }

                try { File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file)); }
                catch { /* время изменения не влияет на работу клиента */ }
            }
        }

        /// <summary>Рекурсивное удаление с повторами (файлы может держать антивирус
        /// или проводник). true — папки больше нет.</summary>
        internal static async Task<bool> TryDeleteDirectoryAsync(string path, int attempts = 5)
        {
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    if (!Directory.Exists(path)) return true;
                    Directory.Delete(path, recursive: true);
                    return true;
                }
                catch (Exception) when (attempt < attempts)
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                }
                catch
                {
                    return false;
                }
            }
            return !Directory.Exists(path);
        }

        internal static string Normalize(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        internal static bool PathsEqual(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try { return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static bool IsSubPath(string path, string root) =>
            path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
