using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;

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
        private static readonly string WindowsDir =
            Environment.GetFolderPath(Environment.SpecialFolder.Windows);
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

        /// <summary>%SystemRoot%\System32\powercfg.exe — часть базовой ОС, путь фиксирован.</summary>
        public static string PowerCfgExe { get; } = Path.Combine(SystemDir, "powercfg.exe");

        /// <summary>%SystemRoot%\System32\shutdown.exe — часть базовой ОС, путь фиксирован.</summary>
        public static string ShutdownExe { get; } = Path.Combine(SystemDir, "shutdown.exe");

        /// <summary>%SystemRoot%\System32\net.exe — часть базовой ОС, путь фиксирован.</summary>
        public static string NetExe { get; } = Path.Combine(SystemDir, "net.exe");

        /// <summary>%SystemRoot%\System32\notepad.exe — часть базовой ОС, путь фиксирован.</summary>
        public static string NotepadExe { get; } = Path.Combine(SystemDir, "notepad.exe");

        /// <summary>%SystemRoot%\System32\cscript.exe — часть базовой ОС, путь фиксирован.</summary>
        public static string CScriptExe { get; } = Path.Combine(SystemDir, "cscript.exe");

        /// <summary>
        /// %SystemRoot%\explorer.exe — исторически лежит НЕ в System32, а в
        /// корне каталога Windows. Путь фиксирован, часть базовой ОС.
        /// </summary>
        public static string ExplorerExe { get; } = Path.Combine(WindowsDir, "explorer.exe");

        /// <summary>
        /// winget — сначала ищем сам пакет App Installer под Program Files\WindowsApps
        /// (см. ResolveWingetFromPackageFolder), и только если не нашли — старый путь
        /// через App Execution Alias в %LocalAppData%\Microsoft\WindowsApps.
        ///
        /// История: раньше здесь резолвился только alias, с доверием по ACL папки
        /// (пентест 2026-07-14 живьём показал, что ACL этой ПЕРсональной папки может
        /// оказаться слабее ожидаемой — обычный пользователь получал Full Control и
        /// мог подменить winget.exe без единого запроса UAC). Аудит 2026-07-17 нашёл,
        /// что сама эвристика ACL недостаточно надёжна В ДРУГУЮ сторону: на живых
        /// машинах (включая как минимум одну без стороннего sandbox-инструмента) эта
        /// папка регулярно оказывается шире узкого предположения "только
        /// SYSTEM/Administrators/TrustedInstaller" без какой-либо реальной
        /// компрометации — резолвинг ложно отказывал для ВСЕГО каталога (любая
        /// запись без прямой ссылки на установщик показывалась "недоступна").
        ///
        /// Попытка спасти alias-путь Authenticode-проверкой самого файла не сработала:
        /// App Execution Alias — не обычный файл, а reparse point (IO_REPARSE_TAG_
        /// APPEXECLINK, 0 байт), и WinVerifyTrust/чтение сертификата с него всегда
        /// падает с CRYPT_E_FILE_ERROR независимо от того, легитимный он или нет —
        /// проверено живьём на этой машине при разработке фикса.
        ///
        /// Правильное решение — резолвить РЕАЛЬНЫЙ файл пакета в Program Files\
        /// WindowsApps\Microsoft.DesktopAppInstaller_*_{arch}__8wekyb3d8bbwe\winget.exe.
        /// Эта папка в принципе не бывает "шире ожидаемого" ни на одной машине — она
        /// заблокирована TrustedInstaller на уровне ОС и не зависит от состояния
        /// конкретного профиля/машины, в отличие от персонального alias. Сам файл там
        /// не reparse point, поэтому Authenticode-проверка на нём работает штатно —
        /// это и есть основной источник доверия для этого пути (см.
        /// ResolveWingetFromPackageFolder), а не ACL.
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

        private static readonly string ProgramFilesDir =
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        private static readonly System.Collections.Generic.Dictionary<string, string?> _packageWingetCache = new();
        private static readonly object _packageWingetCacheLock = new();

        /// <summary>
        /// Находит winget.exe внутри пакета Microsoft.DesktopAppInstaller под
        /// Program Files\WindowsApps и проверяет его Authenticode-подпись
        /// (Microsoft Corporation) — это НЕ reparse point, а настоящий PE-файл,
        /// в отличие от alias'а в профиле пользователя. Возвращает null при любой
        /// проблеме (пакет не найден, доступ к папке ограничен на конкретной
        /// сборке Windows, подпись не подтверждена) — вызывающий код тогда падает
        /// на старый alias-путь. WinVerifyTrust недёшев (проверка отзыва по сети),
        /// поэтому результат кэшируется на процесс: для конкретной версии пакета
        /// содержимое файла не меняется на лету.
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
                    // Может встретиться больше одной версии пакета (например, в окне
                    // между авто-обновлением из Store и очисткой старой) — берём
                    // самую свежую. Сортировка СТРОКОЙ здесь некорректна: "1.9..."
                    // лексически больше "1.27...", можно выбрать более старый пакет.
                    // Парсим версию из имени папки и сравниваем как System.Version;
                    // если формат имени неожиданный (парсинг не удался для обеих
                    // сторон) — откат на строковую сортировку, не хуже прежнего.
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

                        if (AuthenticodeVerifier.IsSignedByMicrosoft(exePath, out string error))
                        {
                            result = exePath;
                            break;
                        }
                        AppLogger.Write($"[TrustedExecutablePaths] ⚠ {exePath}: подпись не подтверждена ({error}) — пропускаю эту версию пакета");
                    }
                }
                catch (Exception ex)
                {
                    // Доступ к Program Files\WindowsApps ограничен на этой сборке
                    // Windows, пакет не найден и т.п. — не фатально, вызывающий
                    // код перейдёт на alias-путь.
                    AppLogger.Write($"[TrustedExecutablePaths] ⚠ Program Files\\WindowsApps недоступен ({ex.GetType().Name}) — резолвинг через пакет пропущен, пробую alias");
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

        /// <summary>
        /// Внутри сборки используется также AppLaunchResolver — Play-кнопка резолвит
        /// exe из InstallLocation HKLM-приложений, каталог которых может оказаться
        /// со слабой ACL (не только App Execution Alias/Chocolatey из этого класса).
        /// </summary>
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

        /// <summary>
        /// Сбрасывает закэшированный результат проверки ACL для одного каталога.
        /// _compromisedCache не имеет TTL — предполагается, что ACL обычной папки не
        /// меняется на лету, что верно почти всегда, КРОМЕ сразу после установки:
        /// официальный установщик Chocolatey может временно расширить/сузить права на
        /// C:\ProgramData\chocolatey\bin в процессе собственной установки — один
        /// неудачно совпавший по времени вызов иначе кэшировал бы «скомпрометировано»
        /// навсегда до перезапуска процесса, даже если реально всё в порядке.
        /// </summary>
        internal static void InvalidateAclCache(string dirPath)
        {
            lock (_compromisedCacheLock)
            {
                _compromisedCache.Remove(dirPath);
            }
        }

        /// <summary>Удобный вызов InvalidateAclCache для конкретно chocolatey\bin —
        /// используется после PackageManagerService.InstallChocoAsync.</summary>
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
