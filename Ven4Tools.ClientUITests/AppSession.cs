using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace Ven4Tools.ClientUITests
{
    /// <summary>
    /// Управляет жизненным циклом запущенного клиента Ven4Tools для UI-тестов.
    /// Запускает .exe, находит главное окно и закрывает приложение.
    ///
    /// Особенности клиента, которые учитываются здесь:
    ///  * приложение требует прав администратора (app.manifest →
    ///    requireAdministrator); если тест-раннер не запущен «от администратора»,
    ///    клиент попытается перезапустить себя через UAC и сменит PID. Поэтому
    ///    главное окно ищется на рабочем столе по заголовку, а не по дескриптору
    ///    исходного процесса.
    ///  * заголовок главного окна — «Ven4Tools».
    /// </summary>
    public sealed class AppSession : IDisposable
    {
        public const string MainWindowTitle = "Ven4Tools";

        private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(15);

        public UIA3Automation Automation { get; }
        public Application? App { get; private set; }
        public Window MainWindow { get; }

        private AppSession(UIA3Automation automation, Application? app, Window mainWindow)
        {
            Automation = automation;
            App = app;
            MainWindow = mainWindow;
        }

        /// <summary>
        /// Запускает клиент и дожидается появления главного окна.
        /// Бросает исключение, если окно не появилось за отведённое время —
        /// вызывающий код переводит тесты в Inconclusive в headless-окружении.
        /// </summary>
        public static AppSession Launch()
        {
            // Клиент — single-instance (Mutex "Ven4Tools.Client.SingleInstance"):
            // если предыдущий процесс (из другого тестового класса) не успел
            // полностью завершиться до этого момента, новый экземпляр молча
            // покажет "Уже запущено" и сам сразу завершится — тогда поиск окна
            // ниже найдёт СТАРОЕ окно с чужими/устаревшими настройками, а не
            // свежий процесс с только что записанными profile.json/source_order.json.
            // Поэтому явно убиваем и ждём завершения всех оставшихся процессов
            // перед стартом нового, вместо того чтобы полагаться только на
            // Dispose() предыдущей сессии (Kill() там не дожидался выхода).
            foreach (var stale in Process.GetProcessesByName("Ven4Tools"))
            {
                try { if (!stale.HasExited) { stale.Kill(); stale.WaitForExit(5000); } } catch { }
                finally { stale.Dispose(); }
            }

            string exePath = ResolveClientExePath();

            var automation = new UIA3Automation();
            Application? app = null;
            try
            {
                app = Application.Launch(exePath);
            }
            catch
            {
                // Запуск мог не выполниться напрямую (например, из-за перезапуска
                // под UAC). Окно всё равно попробуем найти на рабочем столе ниже.
            }

            Window? mainWindow = WaitForMainWindow(automation);
            if (mainWindow == null)
            {
                try { automation.Dispose(); } catch { }
                throw new InvalidOperationException(
                    "Главное окно Ven4Tools не появилось за " + LaunchTimeout.TotalSeconds +
                    " сек. Вероятные причины: окружение без интерактивного рабочего стола " +
                    "(headless) либо отклонённый запрос UAC. Тесты UI требуют запуска " +
                    "из сессии «от имени администратора» с активным рабочим столом.");
            }

            return new AppSession(automation, app, mainWindow);
        }

        /// <summary>Ищет главное окно клиента на рабочем столе по заголовку.</summary>
        private static Window? WaitForMainWindow(UIA3Automation automation)
        {
            var desktop = automation.GetDesktop();
            var found = Retry.WhileNull(
                () =>
                {
                    var el = desktop.FindFirstChild(cf =>
                        cf.ByControlType(ControlType.Window)
                          .And(cf.ByName(MainWindowTitle)));
                    return el?.AsWindow();
                },
                timeout: LaunchTimeout,
                interval: TimeSpan.FromMilliseconds(500),
                throwOnTimeout: false);

            return found.Result;
        }

        /// <summary>
        /// Находит собранный Ven4Tools.exe: сперва Release, затем Debug.
        /// Путь вычисляется относительно расположения сборки тестов.
        /// </summary>
        public static string ResolveClientExePath()
        {
            // Папка решения: ...\Ven4Tools (solution root). Сборка тестов лежит в
            // Ven4Tools.ClientUITests\bin\<Cfg>\net8.0-windows\win-x64\ — поднимаемся к корню.
            string? dir = AppContext.BaseDirectory;
            string? solutionRoot = null;
            var probe = new DirectoryInfo(dir);
            while (probe != null)
            {
                if (File.Exists(Path.Combine(probe.FullName, "Ven4Tools.sln")))
                {
                    solutionRoot = probe.FullName;
                    break;
                }
                probe = probe.Parent;
            }

            solutionRoot ??= Directory.GetCurrentDirectory();

            string clientBin = Path.Combine(solutionRoot, "Ven4Tools", "bin");
            string[] candidates =
            {
                Path.Combine(clientBin, "Release", "net8.0-windows", "win-x64", "Ven4Tools.exe"),
                Path.Combine(clientBin, "Debug",   "net8.0-windows", "win-x64", "Ven4Tools.exe"),
            };

            string? exe = candidates.FirstOrDefault(File.Exists);
            if (exe == null)
            {
                throw new FileNotFoundException(
                    "Не найден собранный Ven4Tools.exe. Ожидались пути:\n" +
                    string.Join("\n", candidates) +
                    "\nСоберите клиент: dotnet build Ven4Tools\\Ven4Tools.csproj -c Release");
            }

            return exe;
        }

        public void Dispose()
        {
            try
            {
                if (MainWindow != null && !MainWindow.IsOffscreen)
                {
                    MainWindow.Close();
                }
            }
            catch { /* окно могло уже закрыться */ }

            // Подстраховка: закрываем/убиваем все процессы клиента, в т.ч.
            // перезапущенный под UAC экземпляр с другим PID.
            try
            {
                foreach (var p in Process.GetProcessesByName("Ven4Tools"))
                {
                    try { if (!p.HasExited) { p.Kill(); p.WaitForExit(5000); } } catch { }
                    p.Dispose();
                }
            }
            catch { }

            try { App?.Dispose(); } catch { }
            try { Automation.Dispose(); } catch { }
        }
    }
}
