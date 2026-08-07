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

        // Резолвинга пути к папке клиента здесь намеренно нет. Он был добавлен как
        // «общий метод для MainWindow и CliInstallRunner», но по факту не вызывался
        // ниоткуда: CliInstallRunner переиспользует сам MainWindow целиком, а тот
        // считает путь у себя (MainWindow.xaml.cs / MainWindow.Download.cs). Второй
        // экземпляр той же логики, в который никто не заходит, опаснее его отсутствия —
        // правку пути сделали бы в нём и не увидели бы никакого эффекта.
    }
}
