using System;
using System.IO;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Общие пути и имена файлов лаунчера — единый источник вместо повторяющихся
    /// строковых литералов, разбросанных по коду.
    /// </summary>
    internal static class LauncherPaths
    {
        // Имя исполняемого файла клиента Ven4Tools.
        public const string ClientExeName = "Ven4Tools.exe";

        // Полный путь к файлу последнего краш-отчёта клиента:
        // %LocalAppData%\Ven4Tools\crash_last.json.
        public static string CrashReportPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "crash_last.json");
    }
}
