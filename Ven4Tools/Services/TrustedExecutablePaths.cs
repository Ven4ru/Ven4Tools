using System;
using System.IO;

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
        /// Эта конкретная папка защищена ACL, запрещающим запись даже
        /// владельцу-пользователю (только TrustedInstaller/System/Administrators
        /// могут туда писать) — именно поэтому Windows использует её для alias'ов
        /// исполняемых файлов. Не искать winget нигде за пределами этого пути:
        /// поиск по PATH/App Paths вернул бы нас к исходной уязвимости.
        /// </summary>
        public static string? ResolveWinget()
        {
            var alias = Path.Combine(LocalAppDataDir, "Microsoft", "WindowsApps", "winget.exe");
            return File.Exists(alias) ? alias : null;
        }

        /// <summary>
        /// Chocolatey по умолчанию ставится в %ProgramData%\chocolatey (права
        /// записи только у администраторов). Намеренно НЕ читаем переменную
        /// окружения ChocolateyInstall — её может выставить на уровне пользователя
        /// не-администратор (setx), и elevated-клиент унаследует её при следующем
        /// запуске, что вернуло бы контроль над путём атакующему.
        /// </summary>
        public static string? ResolveChocolatey()
        {
            var path = Path.Combine(CommonAppDataDir, "chocolatey", "bin", "choco.exe");
            return File.Exists(path) ? path : null;
        }
    }
}
