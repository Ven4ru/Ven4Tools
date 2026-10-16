using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ven4Tools.Helpers;

namespace Ven4Tools.Services
{
    /// <summary>
    /// «Не создавать ярлыки на рабочем столе» (настройка RemoveNewDesktopShortcuts):
    /// запоминает ярлыки рабочего стола до установки и после неё убирает только те,
    /// что появились за это время. На стенде 16.10.2026 полный каталог оставил
    /// на рабочем столе больше 60 ярлыков.
    /// </summary>
    internal static class DesktopShortcutCleaner
    {
        private static readonly string[] Extensions = { ".lnk", ".url" };

        public sealed class Snapshot
        {
            internal Snapshot(HashSet<string> existing, DateTime takenUtc)
            {
                Existing = existing;
                TakenUtc = takenUtc;
            }

            internal HashSet<string> Existing { get; }
            internal DateTime TakenUtc { get; }
        }

        internal static IEnumerable<string> DesktopFolders()
        {
            foreach (var f in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
            {
                string path = Environment.GetFolderPath(f);
                if (!string.IsNullOrEmpty(path)) yield return path;
            }
        }

        public static Snapshot Take() => Take(DesktopFolders());

        internal static Snapshot Take(IEnumerable<string> folders)
            => new(new HashSet<string>(ListShortcuts(folders), StringComparer.OrdinalIgnoreCase), DateTime.UtcNow);

        public static IReadOnlyList<string> RemoveNew(Snapshot snapshot, Action<string>? log)
            => RemoveNew(snapshot, DesktopFolders(), log);

        /// <returns>Пути убранных ярлыков.</returns>
        internal static IReadOnlyList<string> RemoveNew(Snapshot snapshot, IEnumerable<string> folders, Action<string>? log)
        {
            var removed = new List<string>();
            foreach (string file in ListShortcuts(folders))
            {
                if (snapshot.Existing.Contains(file)) continue;
                try
                {
                    // Клиент работает с правами администратора, а рабочий стол доступен на запись
                    // любому процессу пользователя: ссылку-подмену (reparse point) не трогаем.
                    if (PathHelper.IsReparsePoint(file)) continue;
                    // Ярлык, появившийся не во время установки (например, пользователь сохранил
                    // его сам до снимка, а снимок его не увидел), тоже не трогаем.
                    if (File.GetCreationTimeUtc(file) < snapshot.TakenUtc.AddSeconds(-5)) continue;
                    File.Delete(file);
                    removed.Add(file);
                    log?.Invoke($"🧹 Убран ярлык с рабочего стола: {Path.GetFileName(file)}");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"⚠ Не удалось убрать ярлык {Path.GetFileName(file)}: {ex.Message}");
                }
            }
            return removed;
        }

        private static IEnumerable<string> ListShortcuts(IEnumerable<string> folders)
        {
            foreach (string folder in folders)
            {
                if (!Directory.Exists(folder) || PathHelper.IsReparsePoint(folder)) continue;
                string[] files;
                try { files = Directory.GetFiles(folder); }
                catch { continue; }
                foreach (string f in files.Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
                    yield return f;
            }
        }
    }
}
