using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Абсолютные пути системных исполняемых файлов вместо коротких имён.
    /// Своя копия по образцу клиентского Ven4Tools.Services.TrustedExecutablePaths —
    /// общей библиотеки между клиентом и лаунчером нет намеренно (см. MainWindow.PackageManagers.cs).
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

        private static readonly Dictionary<string, string?> _packageWingetCache = new();
        private static readonly object _packageWingetCacheLock = new();

        /// <summary>
        /// Находит winget.exe внутри пакета Microsoft.DesktopAppInstaller под
        /// Program Files\WindowsApps и проверяет его Authenticode-подпись (Microsoft
        /// Corporation) — не reparse point, в отличие от alias'а в профиле пользователя.
        /// Возвращает null при любой проблеме (пакет не найден, доступ ограничен,
        /// подпись не подтверждена) — вызывающий код тогда падает на alias-путь.
        /// Результат кэшируется на процесс: для конкретной версии пакета содержимое
        /// файла не меняется на лету.
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

            string windowsAppsRoot = Path.Combine(ProgramFilesDir, "WindowsApps");

            lock (_packageWingetCacheLock)
            {
                if (_packageWingetCache.TryGetValue(arch, out var cached)) return cached;

                string? result = null;
                try
                {
                    // Публикатор "8wekyb3d8bbwe" — фиксированный хеш издателя Microsoft
                    // для App Installer, одинаков на всех машинах и версиях пакета.
                    var candidates = Directory.GetDirectories(
                        windowsAppsRoot, $"Microsoft.DesktopAppInstaller_*_{arch}__8wekyb3d8bbwe");
                    // Может встретиться больше одной версии пакета — берём самую свежую
                    // по версии из имени папки (строковая сортировка некорректна:
                    // "1.9..." лексически больше "1.27..."). Откат на строковую сортировку,
                    // если формат имени неожиданный.
                    Array.Sort(candidates, (a, b) =>
                    {
                        var va = TryParsePackageVersion(a);
                        var vb = TryParsePackageVersion(b);
                        if (va != null && vb != null) return vb.CompareTo(va);
                        return string.Compare(b, a, StringComparison.OrdinalIgnoreCase);
                    });

                    foreach (var dir in candidates)
                    {
                        var exePath = Path.Combine(dir, "winget.exe");
                        if (!File.Exists(exePath)) continue;

                        if (AuthenticodeVerifier.IsSignedByMicrosoft(exePath, out _))
                        {
                            result = exePath;
                            break;
                        }
                    }
                }
                catch
                {
                    // Доступ к Program Files\WindowsApps ограничен на этой сборке Windows,
                    // пакет не найден и т.п. — не фатально, вызывающий код перейдёт на alias.
                }

                _packageWingetCache[arch] = result;
                return result;
            }
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
        private static readonly object _compromisedCacheLock = new();

        internal static bool IsDirectoryAclCompromised(string dirPath)
        {
            lock (_compromisedCacheLock)
            {
                if (_compromisedCache.TryGetValue(dirPath, out var cached)) return cached;

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

                        compromised = true;
                        break;
                    }
                }
                catch
                {
                    // Не удалось прочитать DACL — fail-closed: считаем каталог скомпрометированным.
                    compromised = true;
                }

                _compromisedCache[dirPath] = compromised;
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
            }
        }

        /// <summary>Удобный вызов InvalidateAclCache для конкретно chocolatey\bin.</summary>
        internal static void InvalidateChocolateyAclCache()
        {
            InvalidateAclCache(Path.Combine(CommonAppDataDir, "chocolatey", "bin"));
        }

        // Только для тестов: позволяет проверить, что запись в кэше действительно
        // отсутствует/присутствует, не меняя поведение резолвинга.
        internal static bool IsAclCacheEntryCached(string dirPath)
        {
            lock (_compromisedCacheLock)
            {
                return _compromisedCache.ContainsKey(dirPath);
            }
        }
    }
}
