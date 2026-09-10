using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Ven4Tools.Shared;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Абсолютные пути системных исполняемых файлов вместо коротких имён.
    /// Своя копия по образцу клиентского Ven4Tools.Services.TrustedExecutablePaths.
    /// AuthenticodeVerifier (которым оба резолвера winget доверяют) уже вынесен в
    /// Shared/ (round 38) — этот класс пока не унифицирован: набор системных exe и
    /// поведение при недоступном ACL (клиент логирует причину отказа, лаунчер молчит)
    /// расходятся между копиями, вынос общего ядра (ResolveWinget/ResolveChocolatey/
    /// TryParsePackageVersion — единственные три метода, идентичные байт-в-байт)
    /// отложен на следующий раунд.
    /// Process.Start с коротким именем ("winget", "choco.exe", "powershell.exe" и т.п.)
    /// ищет файл по порядку Win32 (каталог процесса → текущий каталог → System32 → PATH),
    /// что эксплуатируемо для тех вызовов лаунчера, которые идут в уже-elevated процессе
    /// (Verb=runas / IsRunAsAdmin) — посторонний процесс подкладывает одноимённый файл
    /// в user-writable каталог, откуда запущен лаунчер, и elevated-код запускает его.
    /// </summary>
    internal static class TrustedExecutablePaths
    {
        private static readonly string SystemDir =
            Environment.GetFolderPath(Environment.SpecialFolder.System);
        private static readonly string LocalAppDataDir =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static readonly string CommonAppDataDir =
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        private static readonly string ProgramFilesDir =
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        /// <summary>%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe — путь фиксирован.</summary>
        public static string PowerShellExe { get; } =
            Path.Combine(SystemDir, "WindowsPowerShell", "v1.0", "powershell.exe");

        /// <summary>%SystemRoot%\System32\shutdown.exe — часть базовой ОС, путь фиксирован.</summary>
        public static string ShutdownExe { get; } = Path.Combine(SystemDir, "shutdown.exe");

        /// <summary>
        /// winget — сначала пробуем сам пакет App Installer под Program Files\WindowsApps
        /// (см. ResolveWingetFromPackageFolder), и только если не нашли — старый путь
        /// через App Execution Alias в %LocalAppData%\Microsoft\WindowsApps с ACL-проверкой.
        ///
        /// Портировано из клиентского Ven4Tools.Services.TrustedExecutablePaths.ResolveWinget
        /// (аудит round 16, 2026-07-24) — до этого лаунчер использовал только alias+ACL,
        /// хотя клиентский аудит 2026-07-17 уже показал, что этой эвристики недостаточно
        /// в ОБЕ стороны: пентест 2026-07-14 нашёл случай, когда ACL персональной папки
        /// оказывалась слабее ожидаемой (обычный пользователь мог подменить winget.exe),
        /// а аудит 2026-07-17 — что на живых машинах та же папка регулярно шире узкого
        /// предположения без реальной компрометации (ложный отказ резолвинга). Пакетная
        /// папка Program Files\WindowsApps\Microsoft.DesktopAppInstaller_* заблокирована
        /// TrustedInstaller на уровне ОС независимо от состояния профиля/машины, а сам
        /// winget.exe там — не reparse point, поэтому Authenticode-проверка работает
        /// штатно и служит основным источником доверия вместо ACL. `UpdateBackgroundService`
        /// вызывает ResolveWinget на фоновом таймере — тот же путь резолвинга, что и здесь.
        /// </summary>
        public static string? ResolveWinget()
        {
            var fromPackage = ResolveWingetFromPackageFolder();
            if (fromPackage != null) return fromPackage;

            var dir = Path.Combine(LocalAppDataDir, "Microsoft", "WindowsApps");
            var alias = Path.Combine(dir, "winget.exe");
            if (!File.Exists(alias)) return null;
            return IsDirectoryAclCompromised(dir) ? null : alias;
        }

        private static readonly Dictionary<string, string> _packageWingetCache = new();
        private static readonly object _packageWingetCacheLock = new();

        /// <summary>
        /// Находит winget.exe внутри пакета Microsoft.DesktopAppInstaller под
        /// Program Files\WindowsApps и проверяет его Authenticode-подпись (Microsoft
        /// Corporation) — не reparse point, в отличие от alias'а в профиле пользователя.
        /// Возвращает null при любой проблеме (пакет не найден, доступ ограничен,
        /// подпись не подтверждена) — вызывающий код тогда падает на alias-путь.
        ///
        /// Кэшируется ТОЛЬКО успешный результат: для конкретной версии пакета
        /// содержимое файла не меняется на лету. Отсутствие winget, наоборот,
        /// меняется — и меняет его сам лаунчер (InstallWingetAsync). Раньше в кэш
        /// попадал и null, поэтому после установки winget тем же процессом резолвер
        /// до конца сессии отвечал «не найден», а пользователю показывалось
        /// «Winget не установлен» сколько бы раз он ни нажал «Установить компоненты».
        /// </summary>
        private static string? ResolveWingetFromPackageFolder()
        {
            string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.X64   => "x64",
                System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                System.Runtime.InteropServices.Architecture.X86   => "x86",
                _ => ""
            };
            if (arch.Length == 0) return null;

            lock (_packageWingetCacheLock)
            {
                // Файл мог исчезнуть вместе с обновлением пакета — тогда ищем заново.
                if (_packageWingetCache.TryGetValue(arch, out var cached) && File.Exists(cached))
                    return cached;
                _packageWingetCache.Remove(arch);

                foreach (var dir in EnumeratePackageDirectories(arch))
                {
                    string exePath = Path.Combine(dir, "winget.exe");
                    try
                    {
                        if (!File.Exists(exePath)) continue;
                    }
                    catch { continue; }

                    if (!AuthenticodeVerifier.IsSignedByMicrosoft(exePath, out _)) continue;

                    _packageWingetCache[arch] = exePath;
                    return exePath;
                }

                return null;
            }
        }

        /// <summary>
        /// Каталоги пакета App Installer от свежей версии к старой, без дубликатов.
        ///
        /// Основной источник — реестр AppModel. Обычный (не-elevated) процесс НЕ может
        /// перечислить <c>C:\Program Files\WindowsApps</c>: <see cref="Directory.GetDirectories(string, string)"/>
        /// там бросает <see cref="UnauthorizedAccessException"/> (проверено живьём под
        /// ограниченным токеном). Пройти по УЖЕ ИЗВЕСТНОМУ пути внутрь, прочитать
        /// winget.exe, проверить подпись и запустить его тот же процесс может свободно.
        /// Из-за одного лишь перечисления резолвер у пользователя без прав администратора
        /// полностью зависел от alias'а <c>%LocalAppData%\Microsoft\WindowsApps\winget.exe</c>,
        /// а его может не быть — сразу после установки winget (до перелогина) или когда
        /// «Псевдонимы выполнения приложения» отключены в параметрах Windows. Тогда winget
        /// стоял, работал, но лаунчер его «никогда не видел».
        ///
        /// Перечисление каталога остаётся вторым источником: оно работает у elevated-копии
        /// и там, где записи в реестре по какой-то причине нет.
        /// </summary>
        private static IEnumerable<string> EnumeratePackageDirectories(string arch)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in ReadPackageDirectoriesFromRegistry(arch))
                if (seen.Add(dir)) yield return dir;
            foreach (var dir in ReadPackageDirectoriesFromDisk(arch))
                if (seen.Add(dir)) yield return dir;
        }

        private const string AppModelPackagesKey =
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

        /// <summary>
        /// Пути каталогов пакета App Installer из реестра развёртывания AppX
        /// (значение <c>PackageRootFolder</c>). Ветка HKCU доступна на запись самому
        /// пользователю, поэтому путь оттуда — только подсказка, где искать: принимаются
        /// исключительно каталоги внутри <c>Program Files\WindowsApps</c> (защищён
        /// TrustedInstaller на уровне ОС), а доверие даёт Authenticode-проверка самого
        /// winget.exe у вызывающего кода. Подставить свой exe через реестр так нельзя.
        /// </summary>
        private static List<string> ReadPackageDirectoriesFromRegistry(string arch)
        {
            var result = new List<string>();
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AppModelPackagesKey);
                if (key == null) return result;

                // Публикатор "8wekyb3d8bbwe" — фиксированный хеш издателя Microsoft
                // для App Installer, одинаков на всех машинах и версиях пакета.
                string suffix = $"_{arch}__8wekyb3d8bbwe";
                foreach (string name in key.GetSubKeyNames())
                {
                    if (!name.StartsWith("Microsoft.DesktopAppInstaller_", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

                    using var sub = key.OpenSubKey(name);
                    if (sub?.GetValue("PackageRootFolder") is not string root || root.Length == 0) continue;
                    if (!IsInsideWindowsApps(root)) continue;

                    // Нормализуем: значение из реестра может прийти с завершающим
                    // слэшем, и тогда и разбор версии из имени папки, и сверка с
                    // результатом перечисления каталога промахнулись бы.
                    result.Add(Normalize(root));
                }
            }
            catch
            {
                // Ветка недоступна/повреждена — остаётся перечисление каталога и alias.
            }

            SortByPackageVersionDescending(result);
            return result;
        }

        /// <summary>Путь в сравнимом виде: абсолютный, без завершающего разделителя.</summary>
        private static string Normalize(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
            catch { return path.TrimEnd(Path.DirectorySeparatorChar); }
        }

        private static List<string> ReadPackageDirectoriesFromDisk(string arch)
        {
            var result = new List<string>();
            try
            {
                foreach (string dir in Directory.GetDirectories(
                    Path.Combine(ProgramFilesDir, "WindowsApps"),
                    $"Microsoft.DesktopAppInstaller_*_{arch}__8wekyb3d8bbwe"))
                {
                    result.Add(Normalize(dir));
                }
            }
            catch
            {
                // Перечисление Program Files\WindowsApps запрещено обычному пользователю —
                // штатная ситуация, путь из реестра выше её и закрывает.
            }

            SortByPackageVersionDescending(result);
            return result;
        }

        /// <summary>
        /// Путь лежит внутри <c>%ProgramFiles%\WindowsApps</c> (сам корень не считается).
        /// internal, а не private: это единственный барьер между user-writable подсказкой
        /// из HKCU и запуском файла как winget — он покрыт отдельными тестами.
        /// </summary>
        internal static bool IsInsideWindowsApps(string path)
        {
            try
            {
                string root = Path.GetFullPath(Path.Combine(ProgramFilesDir, "WindowsApps"))
                    .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // Может встретиться больше одной версии пакета — берём самую свежую по версии
        // из имени папки (строковая сортировка некорректна: "1.9..." лексически больше
        // "1.27..."). Откат на строковую сортировку, если формат имени неожиданный.
        private static void SortByPackageVersionDescending(List<string> directories)
        {
            directories.Sort((a, b) =>
            {
                var va = TryParsePackageVersion(a);
                var vb = TryParsePackageVersion(b);
                if (va != null && vb != null) return vb.CompareTo(va);
                return string.Compare(b, a, StringComparison.OrdinalIgnoreCase);
            });
        }

        private static readonly Regex _packageVersionRegex =
            new(@"_(?<ver>\d+(?:\.\d+){1,3})_", RegexOptions.Compiled);

        // Извлекает версию из имени папки пакета вида
        // "Microsoft.DesktopAppInstaller_X.Y.Z.W_arch__8wekyb3d8bbwe". Возвращает
        // null при неожиданном формате имени — вызывающий код тогда откатывается
        // на строковую сортировку.
        private static Version? TryParsePackageVersion(string dirPath)
        {
            string name = Path.GetFileName(dirPath);
            var m = _packageVersionRegex.Match(name);
            return m.Success && Version.TryParse(m.Groups["ver"].Value, out var v) ? v : null;
        }

        /// <summary>
        /// Chocolatey — %ProgramData%\chocolatey\bin\choco.exe. Не читаем переменную
        /// окружения ChocolateyInstall (может выставить не-администратор через setx).
        /// </summary>
        public static string? ResolveChocolatey()
        {
            var dir = Path.Combine(CommonAppDataDir, "chocolatey", "bin");
            var path = Path.Combine(dir, "choco.exe");
            if (!File.Exists(path)) return null;
            return IsDirectoryAclCompromised(dir) ? null : path;
        }

        private const FileSystemRights DangerousWriteRights =
            FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership | FileSystemRights.WriteAttributes |
            FileSystemRights.WriteExtendedAttributes;

        private static readonly SecurityIdentifier LocalSystemSid =
            new(WellKnownSidType.LocalSystemSid, null);
        private static readonly SecurityIdentifier AdministratorsSid =
            new(WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier CreatorOwnerSid =
            new(WellKnownSidType.CreatorOwnerSid, null);
        private const string TrustedInstallerSid =
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

        private static readonly Dictionary<string, bool> _compromisedCache = new();
        // Отдельный кэш для «мягкой» проверки: у неё другой набор допустимых SID,
        // поэтому один словарь на две проверки давал бы ответ от чужого вопроса.
        private static readonly Dictionary<string, bool> _foreignWriteCache = new();
        private static readonly object _compromisedCacheLock = new();

        private static readonly SecurityIdentifier? CurrentUserSid = TryGetCurrentUserSid();

        private static SecurityIdentifier? TryGetCurrentUserSid()
        {
            try { return WindowsIdentity.GetCurrent().User; }
            catch { return null; }
        }

        /// <summary>
        /// Строгая проверка: право записи есть у кого-то, кроме SYSTEM, администраторов,
        /// CREATOR OWNER и TrustedInstaller. Для каталогов, которые обязаны быть
        /// системными (пакет winget, chocolateyin) — там запись самим пользователем
        /// как раз и есть признак подмены.
        /// </summary>
        internal static bool IsDirectoryAclCompromised(string dirPath) =>
            EvaluateAcl(dirPath, _compromisedCache, allowCurrentUser: false);

        /// <summary>
        /// Мягкая проверка для папки установленного клиента: то же самое, но право
        /// записи у САМОГО пользователя нарушением не считается.
        ///
        /// Клиент всегда лежит внутри пользовательского профиля (раньше —
        /// %LocalAppData%\Ven4Tools\Launcher, теперь по умолчанию «Документы»), а там
        /// собственный SID пользователя имеет FullControl всегда и у всех. Строгая
        /// проверка на этом основании считала папку скомпрометированной у КАЖДОГО
        /// пользователя, и «Проверить и восстановить клиент» неизменно показывала
        /// «файлы может изменить любой пользователь этого компьютера» — утверждение,
        /// которое к владельцу собственного профиля просто не относится. Постоянно
        /// горящее предупреждение обесценивает себя ровно тогда, когда права
        /// действительно окажутся ослаблены.
        /// </summary>
        internal static bool IsDirectoryWritableByOtherUsers(string dirPath) =>
            EvaluateAcl(dirPath, _foreignWriteCache, allowCurrentUser: true);

        private static bool EvaluateAcl(string dirPath, Dictionary<string, bool> cache, bool allowCurrentUser)
        {
            lock (_compromisedCacheLock)
            {
                if (cache.TryGetValue(dirPath, out var cached)) return cached;

                bool compromised = false;
                try
                {
                    var acl = new DirectoryInfo(dirPath).GetAccessControl(AccessControlSections.Access);
                    var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier));
                    foreach (FileSystemAccessRule rule in rules)
                    {
                        if (rule.AccessControlType != AccessControlType.Allow) continue;
                        if ((rule.FileSystemRights & DangerousWriteRights) == 0) continue;

                        var sid = (SecurityIdentifier)rule.IdentityReference;
                        if (sid.Equals(LocalSystemSid)) continue;
                        if (sid.Equals(AdministratorsSid)) continue;
                        if (sid.Equals(CreatorOwnerSid)) continue;
                        if (sid.Value == TrustedInstallerSid) continue;
                        if (allowCurrentUser && CurrentUserSid != null && sid.Equals(CurrentUserSid)) continue;

                        compromised = true;
                        break;
                    }
                }
                catch
                {
                    // Не удалось прочитать DACL — fail-closed: считаем каталог скомпрометированным.
                    compromised = true;
                }

                cache[dirPath] = compromised;
                return compromised;
            }
        }

        /// <summary>
        /// Сбрасывает закэшированный результат проверки ACL для одного каталога.
        /// Согласовано с клиентским TrustedExecutablePaths.InvalidateAclCache — см.
        /// подробный комментарий там. Используется после
        /// MainWindow.InstallChocoAsync (установка Chocolatey из лаунчера).
        /// </summary>
        internal static void InvalidateAclCache(string dirPath)
        {
            lock (_compromisedCacheLock)
            {
                _compromisedCache.Remove(dirPath);
                _foreignWriteCache.Remove(dirPath);
            }
        }

        /// <summary>Удобный вызов InvalidateAclCache для конкретно chocolatey\bin.</summary>
        internal static void InvalidateChocolateyAclCache()
        {
            InvalidateAclCache(Path.Combine(CommonAppDataDir, "chocolatey", "bin"));
        }

        /// <summary>
        /// Сбрасывает всё, что резолвер winget мог закэшировать до установки:
        /// найденный путь пакета и вердикт по ACL каталога alias'ов. Вызывать сразу
        /// после установки winget лаунчером (MainWindow.InstallWingetAsync) — до неё
        /// каталог alias'ов мог быть пуст или недоступен, и закэшированный fail-closed
        /// вердикт «скомпрометирован» пережил бы установку до конца сессии.
        /// </summary>
        internal static void InvalidateWingetCache()
        {
            lock (_packageWingetCacheLock)
            {
                _packageWingetCache.Clear();
            }
            InvalidateAclCache(Path.Combine(LocalAppDataDir, "Microsoft", "WindowsApps"));
        }

        // Только для тестов: позволяет проверить, что запись в кэше действительно
        // отсутствует/присутствует, не меняя поведение резолвинга.
        internal static bool IsAclCacheEntryCached(string dirPath)
        {
            lock (_compromisedCacheLock)
            {
                return _compromisedCache.ContainsKey(dirPath) || _foreignWriteCache.ContainsKey(dirPath);
            }
        }
    }
}
