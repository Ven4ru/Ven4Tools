using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

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

        /// <summary>%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe — путь фиксирован.</summary>
        public static string PowerShellExe { get; } =
            Path.Combine(SystemDir, "WindowsPowerShell", "v1.0", "powershell.exe");

        /// <summary>%SystemRoot%\System32\shutdown.exe — часть базовой ОС, путь фиксирован.</summary>
        public static string ShutdownExe { get; } = Path.Combine(SystemDir, "shutdown.exe");

        /// <summary>
        /// winget — App Execution Alias в %LocalAppData%\Microsoft\WindowsApps, защищённой
        /// ACL-папке (см. подробный комментарий в клиентском TrustedExecutablePaths.ResolveWinget).
        /// DACL проверяется явно, а не предполагается — тот же вывод пентеста 2026-07-14.
        /// </summary>
        public static string? ResolveWinget()
        {
            var dir = Path.Combine(LocalAppDataDir, "Microsoft", "WindowsApps");
            var alias = Path.Combine(dir, "winget.exe");
            if (!File.Exists(alias)) return null;
            return IsDirectoryAclCompromised(dir) ? null : alias;
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
