using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>Итог обновления ярлыков после переноса клиента.</summary>
    internal readonly record struct ShortcutRetargetResult(int Updated, IReadOnlyList<string> Failed);

    /// <summary>
    /// Ярлыки клиента после переноса: всё, что указывало внутрь старой папки клиента
    /// (сам Ven4Tools.exe, рабочая папка, значок), перенаправляется в новую с тем же
    /// относительным путём. Ищутся .lnk на рабочем столе, в меню «Пуск» и в
    /// закреплённых на панели задач — текущего пользователя и общие. Общие без прав
    /// администратора не перезаписать: такие попадают в Failed, и лаунчер говорит о
    /// них в журнале, а не молчит.
    /// </summary>
    internal static class ClientShortcutRetargeter
    {
        /// <summary>
        /// Новый путь для пути, лежавшего в старой папке клиента, либо null, если путь
        /// к ней не относится. Чистая функция — покрыта unit-тестами.
        /// </summary>
        internal static string? MapPath(string? path, string oldClientPath, string newClientPath)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string old = ClientFolderMover.Normalize(oldClientPath);
            string full;
            try { full = Path.GetFullPath(path.Trim().Trim('"')); }
            catch { return null; }

            if (string.Equals(full.TrimEnd('\\'), old, StringComparison.OrdinalIgnoreCase))
                return ClientFolderMover.Normalize(newClientPath);
            if (!full.StartsWith(old + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return null;
            return Path.Combine(ClientFolderMover.Normalize(newClientPath), full.Substring(old.Length + 1));
        }

        /// <summary>
        /// Расположение значка «путь,индекс» — переносится только путь.
        /// </summary>
        internal static string? MapIconLocation(string? iconLocation, string oldClientPath, string newClientPath)
        {
            if (string.IsNullOrWhiteSpace(iconLocation)) return null;
            int comma = iconLocation.LastIndexOf(',');
            string file = comma > 0 ? iconLocation.Substring(0, comma) : iconLocation;
            string suffix = comma > 0 ? iconLocation.Substring(comma) : "";
            string? mapped = MapPath(file, oldClientPath, newClientPath);
            return mapped == null ? null : mapped + suffix;
        }

        /// <summary>Папки, где ищутся ярлыки клиента.</summary>
        internal static IEnumerable<(string Directory, bool Recursive)> ShortcutLocations()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            yield return (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false);
            yield return (Environment.GetFolderPath(Environment.SpecialFolder.Programs), true);
            yield return (Path.Combine(appData, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned"), true);
            yield return (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), false);
            yield return (Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), true);
        }

        public static ShortcutRetargetResult Retarget(string oldClientPath, string newClientPath, Action<string>? log)
        {
            int updated = 0;
            var failed = new List<string>();

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false);
            if (shellType == null)
            {
                log?.Invoke("⚠️ Ярлыки не обновлены: оболочка Windows недоступна");
                return new ShortcutRetargetResult(0, failed);
            }

            object? shell = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                if (shell == null) return new ShortcutRetargetResult(0, failed);
                dynamic wsh = shell;

                foreach (var (dir, recursive) in ShortcutLocations())
                {
                    if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
                    foreach (string lnk in EnumerateShortcuts(dir, recursive))
                    {
                        object? shortcut = null;
                        try
                        {
                            shortcut = wsh.CreateShortcut(lnk);
                            dynamic s = shortcut!;
                            string? newTarget = MapPath((string?)s.TargetPath, oldClientPath, newClientPath);
                            if (newTarget == null) continue;

                            s.TargetPath = newTarget;
                            string? newWorkDir = MapPath((string?)s.WorkingDirectory, oldClientPath, newClientPath);
                            if (newWorkDir != null) s.WorkingDirectory = newWorkDir;
                            string? newIcon = MapIconLocation((string?)s.IconLocation, oldClientPath, newClientPath);
                            if (newIcon != null) s.IconLocation = newIcon;
                            s.Save();
                            updated++;
                        }
                        catch
                        {
                            failed.Add(lnk);
                        }
                        finally
                        {
                            // Отдельный COM-объект на каждый ярлык — освобождаем сразу,
                            // а не ждём сборщика (см. то же в клиенте, AppLaunchResolver).
                            if (shortcut != null && Marshal.IsComObject(shortcut))
                            {
                                try { Marshal.ReleaseComObject(shortcut); } catch { }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"⚠️ Обновление ярлыков прервано: {ex.Message}");
            }
            finally
            {
                if (shell != null && Marshal.IsComObject(shell))
                {
                    try { Marshal.ReleaseComObject(shell); } catch { }
                }
            }

            return new ShortcutRetargetResult(updated, failed);
        }

        private static IEnumerable<string> EnumerateShortcuts(string dir, bool recursive)
        {
            // Недоступная подпапка меню «Пуск» не должна прерывать обход остальных.
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            try
            {
                return Directory.EnumerateFiles(dir, "*.lnk", options);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
