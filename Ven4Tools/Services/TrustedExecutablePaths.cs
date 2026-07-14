using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Абсолютные пути системных исполняемых файлов вместо коротких имён.
    /// Клиент всегда elevated (requireAdministrator): Process.Start с коротким
    /// именем ("winget", "choco.exe", "cmd.exe" и т.п.) ищет файл по порядку
    /// Win32 (каталог процесса → текущий каталог → System32 → PATH), что
    /// эксплуатируемо, если сам клиент запущен из каталога, доступного на
    /// запись не-администратору (например Downloads) — посторонний процесс
    /// подкладывает туда одноимённый файл, и elevated-клиент запускает его.
    /// </summary>
    internal static class TrustedExecutablePaths
    {
        private static readonly string SystemDir =
            Environment.GetFolderPath(Environment.SpecialFolder.System);
        private static readonly string LocalAppDataDir =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static readonly string CommonAppDataDir =
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        /// <summary>%SystemRoot%\System32\cmd.exe — часть базовой ОС, путь фиксирован.</summary>
        public static string CmdExe { get; } = Path.Combine(SystemDir, "cmd.exe");

        /// <summary>%SystemRoot%\System32\msiexec.exe — часть базовой ОС, путь фиксирован.</summary>
        public static string MsiExec { get; } = Path.Combine(SystemDir, "msiexec.exe");

        /// <summary>%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe — путь фиксирован.</summary>
        public static string PowerShellExe { get; } =
            Path.Combine(SystemDir, "WindowsPowerShell", "v1.0", "powershell.exe");

        /// <summary>
        /// winget — App Execution Alias в %LocalAppData%\Microsoft\WindowsApps.
        /// По дизайну Windows эта папка защищена ACL, запрещающим запись даже
        /// владельцу-пользователю (только TrustedInstaller/System/Administrators
        /// могут туда писать) — именно поэтому Windows использует её для alias'ов
        /// исполняемых файлов. Не искать winget нигде за пределами этого пути:
        /// поиск по PATH/App Paths вернул бы нас к исходной уязвимости.
        ///
        /// Это предположение НЕ принимается на веру: пентест 2026-07-14 живьём
        /// показал, что на реальной машине ACL этой папки может оказаться слабее
        /// ожидаемой (сторонний sandbox-инструмент, повреждённый профиль, более
        /// ранняя компрометация и т.п.) — обычный пользователь получал Full
        /// Control и мог подменить winget.exe без единого запроса UAC. Поэтому
        /// перед доверием пути DACL проверяется явно (см. IsDirectoryAclCompromised).
        ///
        /// Важно: проверка читает DACL, а НЕ пробует реально записать файл —
        /// живая проба записи бесполезна именно здесь, потому что сам клиент
        /// всегда elevated (requireAdministrator), и Administrators ЗАКОННО
        /// имеют право записи в эту папку. Проба «могу ли я, elevated-процесс,
        /// сюда писать» всегда была бы true и перманентно ломала бы резолвинг
        /// winget на КАЖДОЙ машине независимо от реального состояния ACL —
        /// эта ошибка была найдена и исправлена в тот же день, при повторном
        /// тестировании фикса из по-настоящему де-элевированного процесса.
        /// </summary>
        public static string? ResolveWinget()
        {
            var dir = Path.Combine(LocalAppDataDir, "Microsoft", "WindowsApps");
            var alias = Path.Combine(dir, "winget.exe");
            if (!File.Exists(alias)) return null;
            return IsDirectoryAclCompromised(dir) ? null : alias;
        }

        /// <summary>
        /// Chocolatey по умолчанию ставится в %ProgramData%\chocolatey (права
        /// записи только у администраторов). Намеренно НЕ читаем переменную
        /// окружения ChocolateyInstall — её может выставить на уровне пользователя
        /// не-администратор (setx), и elevated-клиент унаследует её при следующем
        /// запуске, что вернуло бы контроль над путём атакующему. Та же живая
        /// проверка ACL, что и для winget (см. ResolveWinget) — на случай, если
        /// %ProgramData% тоже окажется ослаблена на конкретной машине.
        /// </summary>
        public static string? ResolveChocolatey()
        {
            var dir = Path.Combine(CommonAppDataDir, "chocolatey", "bin");
            var path = Path.Combine(dir, "choco.exe");
            if (!File.Exists(path)) return null;
            return IsDirectoryAclCompromised(dir) ? null : path;
        }

        // Права, дающие возможность создать/изменить/удалить файл в каталоге —
        // именно этого набора достаточно для подмены исполняемого файла.
        private const FileSystemRights DangerousWriteRights =
            FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership | FileSystemRights.WriteAttributes |
            FileSystemRights.WriteExtendedAttributes;

        private static readonly SecurityIdentifier LocalSystemSid =
            new(WellKnownSidType.LocalSystemSid, null);
        private static readonly SecurityIdentifier AdministratorsSid =
            new(WellKnownSidType.BuiltinAdministratorsSid, null);
        // CREATOR OWNER — SID-плейсхолдер, не идентифицирует ни один реальный
        // токен доступа сам по себе (только шаблон для наследуемых ACE, которые
        // при наследовании заменяются на SID фактического владельца файла).
        // Часто встречается унаследованным на %ProgramData%-подобных папках —
        // без этого исключения такой ACE ложно считался бы компрометацией.
        private static readonly SecurityIdentifier CreatorOwnerSid =
            new(WellKnownSidType.CreatorOwnerSid, null);
        // У TrustedInstaller нет WellKnownSidType в .NET — сравниваем по строковому SID
        // (одинаков на всех системах, не зависит от локали/имени учётной записи).
        private const string TrustedInstallerSid =
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

        /// <summary>
        /// Проверяет DACL каталога напрямую (не пробует реально писать —
        /// см. комментарий у ResolveWinget о том, почему живая проба записи
        /// здесь бесполезна). Компрометацией считается любое ACE типа Allow,
        /// дающее право записи учётной записи, ОТЛИЧНОЙ от SYSTEM,
        /// Administrators или TrustedInstaller — именно эти три и только эти
        /// три легитимно должны иметь право записи в такую папку.
        /// Результат кэшируется на процесс: ACL каталога не меняется на лету.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, bool> _compromisedCache = new();
        private static readonly object _compromisedCacheLock = new();

        private static bool IsDirectoryAclCompromised(string dirPath)
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

                    if (compromised)
                        AppLogger.Write($"[TrustedExecutablePaths] ⚠ {dirPath}: DACL разрешает запись учётной записи, отличной от SYSTEM/Administrators/TrustedInstaller — резолвинг отклонён, fail-closed");
                }
                catch (Exception ex)
                {
                    // Не удалось прочитать DACL (не сбой самой проверки — например,
                    // нет прав READ_CONTROL) — не можем подтвердить безопасность,
                    // fail-closed: считаем каталог скомпрометированным. Отдельное
                    // сообщение в лог, чтобы не приписывать коду находку, которую
                    // он на самом деле не смог сделать.
                    AppLogger.Write($"[TrustedExecutablePaths] ⚠ {dirPath}: не удалось прочитать DACL ({ex.GetType().Name}) — резолвинг отклонён, fail-closed");
                    compromised = true;
                }

                _compromisedCache[dirPath] = compromised;
                return compromised;
            }
        }
    }
}
