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

        /// <summary>
        /// Путь к папке клиента: из настроек (launcher_settings.json → InstallPath),
        /// либо, если не задан, рядом с исполняемым файлом лаунчера — та же логика,
        /// что использует MainWindow при старте. Общий метод, чтобы CliInstallRunner
        /// не дублировал резолвинг мимо MainWindow и не расходился с ним.
        /// </summary>
        public static string ResolveClientPath()
        {
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
            string settingsPath = Path.Combine(appData, "launcher_settings.json");
            string installPath = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                if (File.Exists(settingsPath))
                {
                    var json = File.ReadAllText(settingsPath);
                    var settings = Newtonsoft.Json.Linq.JObject.Parse(json);
                    string? fromSettings = settings["InstallPath"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(fromSettings)) installPath = fromSettings;
                }
            }
            catch { }
            return Path.Combine(installPath, "Ven4Tools_Client");
        }
    }
}
