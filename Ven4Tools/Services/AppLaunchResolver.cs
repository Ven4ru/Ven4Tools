using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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

        // Прогревает индекс на фоновом потоке. Первый вызов TryResolve после
        // InvalidateCache иначе строит индекс синхронно (полное перечисление реестра +
        // .lnk-файлов Start Menu + COM на каждый ярлык) на том потоке, откуда его
        // позвали — если это UI-поток, интерфейс подвисает на время скана.
        //
        // ВАЖНО: строим индекс на выделенном потоке с ApartmentState.STA, а не через
        // Task.Run. ScanStartMenuShortcuts создаёт COM-объект WScript.Shell, который
        // рассчитан на STA-апартамент (как раньше, когда индекс строился прямо на
        // UI-потоке WPF — тот всегда STA). Поток из пула .NET по умолчанию MTA:
        // создание/использование этого COM-объекта из MTA-потока нестабильно — может
        // кинуть COMException, а может тихо повиснуть на маршалинге через STA-прокси.
        // Исключение из GetOrBuildIndex прокидываем в возвращаемый Task как Faulted,
        // чтобы оно не потерялось (см. защитное логирование в CatalogViewModel).
        public static Task EnsureIndexBuiltAsync()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { GetOrBuildIndex(); tcs.SetResult(); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return tcs.Task;
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

                            // Тот же fail-closed ACL-контроль, что и в
                            // ScanUninstallInstallLocations: App Paths регистрируется
                            // админом, но указывает на произвольный каталог, который может
                            // иметь слабую ACL. Play-кнопка запускает exe в elevated-клиенте —
                            // если каталог с exe доступен на запись непривилегированному
                            // пользователю, бинарник можно подменить. Проверяем каталог exe
                            // ДО принятия пути; каталог не определить — тоже отклоняем.
                            string? appPathDir = Path.GetDirectoryName(path);
                            if (string.IsNullOrEmpty(appPathDir) ||
                                TrustedExecutablePaths.IsDirectoryAclCompromised(appPathDir))
                                continue;

                            string nameHint = GetExeNameHint(path) ?? Path.GetFileNameWithoutExtension(path);
                            result.Add(new Candidate(Normalize(nameHint), path));
                        }
                        catch { /* один битый ключ не должен рушить весь скан */ }
                    }
                }
                catch { /* ветка реестра недоступна целиком — этот источник просто не даёт кандидатов */ }
            }
            return result;
        }

        // 2) Системные ярлыки Start Menu (%ProgramData%, НЕ %AppData% пользователя).
        private static IEnumerable<Candidate> ScanStartMenuShortcuts()
        {
            var result = new List<Candidate>();
            string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
            if (!Directory.Exists(root)) return result;

            // Собственный обход вместо Directory.EnumerateFiles(..., AllDirectories):
            // встроенный AllDirectories — ленивый (реальный рекурсивный обход идёт при
            // итерации в foreach ниже, а не при вызове), поэтому try/catch вокруг вызова
            // НЕ ловил бы исключение, вылетающее при обходе конкретной подпапки в глубине.
            // Плюс AllDirectories не умеет пропускать недоступную подпапку — единственная
            // папка, к которой на мгновение нет доступа (антивирус/установщик/синхронизатор
            // заблокировал её ровно в момент скана), обрывала бы весь обход и вместе с ним
            // весь UpdateInstalledStatusAsync. EnumerateLnkFilesSafe ловит недоступность на
            // каждом уровне отдельно и просто пропускает проблемную папку, не роняя дерево.
            var lnkFiles = EnumerateLnkFilesSafe(root);

            // Один WScript.Shell на весь проход индексации — раньше создавался заново
            // на каждый .lnk и никогда не освобождался (сотни висящих COM-объектов
            // при большом системном Start Menu).
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return result;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return result;

            try
            {
                foreach (var lnk in lnkFiles)
                {
                    try
                    {
                        string? targetPath = ResolveShortcutTarget(shell, lnk);
                        if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath)) continue;
                        if (!targetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                        // Тот же fail-closed ACL-контроль, что и у двух других источников:
                        // системный .lnk создаётся админом, но его цель может лежать в
                        // каталоге со слабой ACL; иначе exe в нём можно подменить перед
                        // запуском в elevated-клиенте. Проверяем каталог цели до принятия.
                        string? targetDir = Path.GetDirectoryName(targetPath);
                        if (string.IsNullOrEmpty(targetDir) ||
                            TrustedExecutablePaths.IsDirectoryAclCompromised(targetDir))
                            continue;

                        string nameHint = Path.GetFileNameWithoutExtension(lnk);
                        result.Add(new Candidate(Normalize(nameHint), targetPath));
                    }
                    catch { /* один битый ярлык не должен рушить весь скан */ }
                }
            }
            finally { Marshal.FinalReleaseComObject(shell); }

            return result;
        }

        // Рекурсивный обход .lnk-файлов, устойчивый к недоступным подпапкам. В отличие
        // от Directory.EnumerateFiles(..., AllDirectories), здесь недоступность отдельной
        // папки (постоянная или транзитивная) пропускается, а остальное дерево сканируется
        // дальше. Directory.GetFiles/GetDirectories — НЕ ленивые: выполняются сразу и
        // бросают исключение сразу же, поэтому try/catch вокруг них реально работает.
        // Пропуск недоступной папки — штатная, ожидаемая ситуация (не ошибка уровня ❌),
        // поэтому логировать её не нужно — молчаливый skip корректен.
        private static IEnumerable<string> EnumerateLnkFilesSafe(string root)
        {
            var result = new List<string>();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();

                string[] files;
                try { files = Directory.GetFiles(dir, "*.lnk"); }
                catch { continue; } // недоступна сама папка — пропускаем её файлы, не всё дерево
                result.AddRange(files);

                string[] subDirs;
                try { subDirs = Directory.GetDirectories(dir); }
                catch { continue; } // недоступен список подпапок — глубже не идём отсюда, но остальное дерево не страдает
                foreach (var sub in subDirs) stack.Push(sub);
            }
            return result;
        }

        // COM-позднее связывание с WScript.Shell — без добавления COM-ссылки в csproj.
        //
        // Сам объект ярлыка тоже обязателен к освобождению. Выше для WScript.Shell это
        // уже сделано («сотни висящих COM-объектов при большом системном Start Menu»),
        // но CreateShortcut возвращает ОТДЕЛЬНЫЙ COM-объект на каждый .lnk, и он не
        // освобождался — ровно тот же самый леак, просто уровнем ниже: один общий Shell
        // вместо сотен, но по-прежнему сотни объектов ярлыков, живущих до финализатора.
        private static string? ResolveShortcutTarget(dynamic shell, string lnkPath)
        {
            object? shortcut = null;
            try
            {
                shortcut = shell.CreateShortcut(lnkPath);
                string target = ((dynamic)shortcut!).TargetPath;
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            catch { return null; }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut))
                {
                    try { Marshal.FinalReleaseComObject(shortcut); } catch { }
                }
            }
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

                            // InstallLocation приходит из HKLM (сам ключ Uninstall защищён),
                            // но каталог, на который он указывает, не обязан быть Program
                            // Files — некоторые инсталляторы кладут его в ProgramData или
                            // иной путь со слабой ACL. Play-кнопка запускает найденный exe
                            // в elevated-клиенте — тот же класс риска, что и у System32-
                            // бинарников в TrustedExecutablePaths: fail-closed, если каталог
                            // разрешает запись кому-то, кроме SYSTEM/Administrators/TrustedInstaller.
                            if (TrustedExecutablePaths.IsDirectoryAclCompromised(installLocation))
                                continue;

                            string? exe = FindBestExeInDirectory(installLocation, displayName);
                            if (exe != null)
                                result.Add(new Candidate(Normalize(displayName), exe));
                        }
                        catch { /* один битый ключ не должен рушить весь скан */ }
                    }
                }
                catch { /* ветка реестра недоступна целиком — этот источник просто не даёт кандидатов */ }
            }
            return result;
        }

        private static readonly Regex ExcludedExeNames = new(
            @"unins(tall)?|setup|update|crashpad|helper|uninst",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // internal, а не private: выбор основного exe среди нескольких — чистая
        // функция от содержимого каталога, и проверить её можно на временной папке
        // (тест живёт в Ven4Tools.Tests, см. InternalsVisibleTo).
        internal static string? FindBestExeInDirectory(string installLocation, string displayName)
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
                // Пустое нормализованное имя отсекается явно: Normalize возвращает ""
                // для файла без единой буквы или цифры в имени («-.exe», «_.exe»), а
                // string.Contains("") истинно ВСЕГДА — такой файл выигрывал сопоставление
                // по имени у любого настоящего кандидата и запускался вместо приложения.
                var byNameMatch = exeFiles.FirstOrDefault(f =>
                {
                    string exeName = Normalize(Path.GetFileNameWithoutExtension(f));
                    return exeName.Length > 0 && normalizedName.Contains(exeName);
                });
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
