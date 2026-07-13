using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Ven4Tools.Services
{
    // Резолвит исполняемый файл уже установленного приложения по его отображаемому
    // имени — нужно для кнопки "▶ Запустить" в каталоге. Winget/Chocolatey не отдают
    // путь к exe напрямую, поэтому ищем среди источников, которые сам инсталлятор
    // обычно регистрирует для Windows.
    //
    // Намеренно смотрим ТОЛЬКО в HKLM и системный (не пользовательский) Start Menu:
    // клиент Ven4Tools всегда работает с правами администратора (app.manifest), и
    // дочерний процесс наследует повышенный токен. Если бы резолвер доверял
    // HKCU/%AppData% (доступны на запись любому непривилегированному процессу того
    // же пользователя), это был бы тот же класс уязвимости, что и HIGH-1 из аудита
    // безопасности 2026-07-13 (непривилегированные данные → elevated-действие без
    // проверки). Резолвер должен быть fail-closed: не нашли уверенного совпадения —
    // кнопка просто не показывается, никогда не гадаем.
    public static class AppLaunchResolver
    {
        private sealed record Candidate(string NormalizedName, string ExePath);

        private static List<Candidate>? _index;
        private static readonly object _lock = new();

        public static string? TryResolve(string displayName)
        {
            var index = GetOrBuildIndex();
            string target = Normalize(displayName);
            if (target.Length == 0) return null;

            // Точное совпадение сначала, затем совпадение по вхождению (в обе стороны),
            // только если совпадающая часть достаточно длинная — иначе слишком много
            // ложных срабатываний на коротких названиях вроде "Notes"/"Mail".
            var exact = index.FirstOrDefault(c => c.NormalizedName == target);
            if (exact != null) return File.Exists(exact.ExePath) ? exact.ExePath : null;

            Candidate? best = null;
            foreach (var c in index)
            {
                if (c.NormalizedName.Length < 4 || target.Length < 4) continue;
                bool contains = c.NormalizedName.Contains(target) || target.Contains(c.NormalizedName);
                if (!contains) continue;
                if (best == null || c.NormalizedName.Length < best.NormalizedName.Length)
                    best = c; // предпочитаем более короткое/точное совпадение
            }

            return best != null && File.Exists(best.ExePath) ? best.ExePath : null;
        }

        // Сбрасывает кэш индекса — вызвать после свежей установки/удаления приложения,
        // иначе резолвер продолжит смотреть на снимок реестра/ярлыков на момент
        // первого запроса в рамках текущего процесса.
        public static void InvalidateCache()
        {
            lock (_lock) { _index = null; }
        }

        private static List<Candidate> GetOrBuildIndex()
        {
            lock (_lock)
            {
                if (_index != null) return _index;

                var candidates = new List<Candidate>();
                candidates.AddRange(ScanAppPaths());
                candidates.AddRange(ScanStartMenuShortcuts());
                candidates.AddRange(ScanUninstallInstallLocations());

                _index = candidates;
                return _index;
            }
        }

        // 1) HKLM App Paths — многие инсталляторы кладут сюда прямую ссылку на exe.
        // Ключ реестра — само имя exe, не отображаемое имя продукта, поэтому для
        // сопоставления с каталогом читаем FileDescription/ProductName самого exe.
        private static IEnumerable<Candidate> ScanAppPaths()
        {
            var result = new List<Candidate>();
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var appPaths = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                    if (appPaths == null) continue;

                    foreach (var subName in appPaths.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = appPaths.OpenSubKey(subName);
                            string? path = sub?.GetValue(null) as string; // (Default) значение
                            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;

                            string nameHint = GetExeNameHint(path) ?? Path.GetFileNameWithoutExtension(path);
                            result.Add(new Candidate(Normalize(nameHint), path));
                        }
                        catch { /* один битый ключ не должен рушить весь скан */ }
                    }
                }
                catch { }
            }
            return result;
        }

        // 2) Системные ярлыки Start Menu (%ProgramData%, НЕ %AppData% пользователя).
        private static IEnumerable<Candidate> ScanStartMenuShortcuts()
        {
            var result = new List<Candidate>();
            string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
            if (!Directory.Exists(root)) return result;

            IEnumerable<string> lnkFiles;
            try { lnkFiles = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch { return result; }

            foreach (var lnk in lnkFiles)
            {
                try
                {
                    string? targetPath = ResolveShortcutTarget(lnk);
                    if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath)) continue;
                    if (!targetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                    string nameHint = Path.GetFileNameWithoutExtension(lnk);
                    result.Add(new Candidate(Normalize(nameHint), targetPath));
                }
                catch { }
            }
            return result;
        }

        // COM-позднее связывание с WScript.Shell — без добавления COM-ссылки в csproj.
        private static string? ResolveShortcutTarget(string lnkPath)
        {
            try
            {
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell == null) return null;
                dynamic shortcut = shell.CreateShortcut(lnkPath);
                string target = shortcut.TargetPath;
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            catch { return null; }
        }

        // 3) HKLM Uninstall → InstallLocation + эвристический поиск exe в папке,
        // только если первые два способа не дали кандидата для этого продукта.
        private static IEnumerable<Candidate> ScanUninstallInstallLocations()
        {
            var result = new List<Candidate>();
            string[] uninstallKeys =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (var keyPath in uninstallKeys)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using var uninstall = baseKey.OpenSubKey(keyPath);
                    if (uninstall == null) continue;

                    foreach (var subName in uninstall.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = uninstall.OpenSubKey(subName);
                            string? displayName = sub?.GetValue("DisplayName") as string;
                            string? installLocation = sub?.GetValue("InstallLocation") as string;
                            if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(installLocation))
                                continue;
                            if (!Directory.Exists(installLocation)) continue;

                            string? exe = FindBestExeInDirectory(installLocation, displayName);
                            if (exe != null)
                                result.Add(new Candidate(Normalize(displayName), exe));
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return result;
        }

        private static readonly Regex ExcludedExeNames = new(
            @"unins(tall)?|setup|update|crashpad|helper|uninst",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string? FindBestExeInDirectory(string installLocation, string displayName)
        {
            try
            {
                var exeFiles = Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly)
                    .Where(f => !ExcludedExeNames.IsMatch(Path.GetFileNameWithoutExtension(f)))
                    .ToList();
                if (exeFiles.Count == 0) return null;
                if (exeFiles.Count == 1) return exeFiles[0];

                // Несколько кандидатов — предпочитаем тот, чьё имя пересекается
                // с названием продукта, иначе берём крупнейший (обычно основной exe,
                // а не служебные утилиты рядом).
                string normalizedName = Normalize(displayName);
                var byNameMatch = exeFiles.FirstOrDefault(f =>
                    normalizedName.Contains(Normalize(Path.GetFileNameWithoutExtension(f))));
                if (byNameMatch != null) return byNameMatch;

                return exeFiles.OrderByDescending(f => new FileInfo(f).Length).First();
            }
            catch { return null; }
        }

        private static string? GetExeNameHint(string exePath)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exePath);
                return !string.IsNullOrWhiteSpace(info.FileDescription)
                    ? info.FileDescription
                    : info.ProductName;
            }
            catch { return null; }
        }

        private static string Normalize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }
    }
}
