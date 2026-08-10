// Services/LauncherInstallation.cs
using System;
using System.Diagnostics;
using System.IO;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Сведения об установке лаунчера: где он должен стоять, откуда фактически
    /// запущен текущий процесс и какой он версии.
    ///
    /// Отдельно от <see cref="LauncherUpdateService"/>: к скачиванию и запуску
    /// установщика это отношения не имеет, зато нужно обеим половинам обновления
    /// (проверке — чтобы знать текущую версию, установке — чтобы понять, нужно ли
    /// вообще предлагать установку).
    /// </summary>
    public static class LauncherInstallation
    {
        /// <summary>Имя exe-файла лаунчера.</summary>
        public const string ExeName = "Ven4Tools.Launcher.exe";

        /// <summary>Папка установки лаунчера: %LOCALAPPDATA%\Ven4Tools\Launcher.</summary>
        public static string InstallDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "Launcher");

        /// <summary>Полный путь к установленному exe лаунчера.</summary>
        public static string InstalledExePath { get; } = Path.Combine(InstallDir, ExeName);

        /// <summary>
        /// Текущая версия лаунчера в формате X.Y.Z (из метаданных сборки).
        /// </summary>
        public static string GetCurrentVersion()
        {
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "0.0.0";
        }

        /// <summary>
        /// Путь к exe текущего процесса (или пустая строка, если определить не удалось).
        /// </summary>
        public static string GetCurrentExePath()
        {
            try
            {
                return Process.GetCurrentProcess().MainModule?.FileName ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Запущен ли лаунчер из папки установки %LOCALAPPDATA%\Ven4Tools\Launcher.
        /// </summary>
        public static bool IsRunningFromInstallDir()
        {
            string exePath = GetCurrentExePath();
            if (string.IsNullOrEmpty(exePath)) return false;

            try
            {
                string currentDir = Path.GetFullPath(Path.GetDirectoryName(exePath) ?? "")
                                        .TrimEnd(Path.DirectorySeparatorChar);
                string installDir = Path.GetFullPath(InstallDir)
                                        .TrimEnd(Path.DirectorySeparatorChar);
                return string.Equals(currentDir, installDir, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
