using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.Capturing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ven4Tools.ClientUITests
{
    /// <summary>
    /// Следы упавшего теста: снимок экрана, список окон на рабочем столе и хвост
    /// журнала клиента.
    ///
    /// Сообщение «элемент не найден» само по себе не говорит, что было на экране:
    /// окно клиента могло ещё грузиться, поверх него мог висеть чужой диалог, а
    /// клиент — упасть. На своей машине это видно глазами, на раннере — нет, и без
    /// следов падение там остаётся догадкой. Файлы складываются в
    /// <c>TestResults\ClientUI\failures</c> — оттуда их забирает «Проверка интерфейса».
    /// </summary>
    [TestClass]
    public sealed class FailureDiagnostics
    {
        private const int LogTailLines = 80;

        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "app.log");

        [GlobalTestCleanup]
        public static void AfterEachTest(TestContext context)
        {
            if (context.CurrentTestOutcome is UnitTestOutcome.Passed or UnitTestOutcome.Inconclusive) return;

            // Сбор следов не должен менять исход теста: любая ошибка здесь проглатывается.
            try
            {
                string dir = Path.Combine(ResolveSolutionRoot(), "TestResults", "ClientUI", "failures");
                Directory.CreateDirectory(dir);
                string shell = AppSession.SuiteUsesModernShell ? "modern" : "classic";
                string baseName = Path.Combine(dir, shell + "-" + SafeFileName(context.TestName ?? "test"));

                try
                {
                    using var image = Capture.Screen();
                    image.ToFile(baseName + ".png");
                }
                catch (Exception ex)
                {
                    context.WriteLine("Снимок экрана не сделан: " + ex.Message);
                }

                var report = new StringBuilder();
                report.AppendLine($"{context.FullyQualifiedTestClassName}.{context.TestName} — {context.CurrentTestOutcome}");
                report.AppendLine($"Время (UTC): {DateTime.UtcNow:o}");
                report.AppendLine();
                report.AppendLine("Окна на рабочем столе:");
                report.AppendLine(DescribeWindows());
                report.AppendLine($"Журнал клиента, последние {LogTailLines} строк:");
                report.AppendLine(ReadLogTail());

                File.WriteAllText(baseName + ".txt", report.ToString(), new UTF8Encoding(false));
                context.WriteLine(report.ToString());
            }
            catch { }
        }

        private static string ResolveSolutionRoot()
        {
            var probe = new DirectoryInfo(AppContext.BaseDirectory);
            while (probe != null)
            {
                if (File.Exists(Path.Combine(probe.FullName, "Ven4Tools.sln"))) return probe.FullName;
                probe = probe.Parent;
            }
            return Directory.GetCurrentDirectory();
        }

        private static string SafeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        private static string ReadLogTail()
        {
            try
            {
                using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                string[] lines = reader.ReadToEnd().Split('\n');
                return string.Join("\n", lines.Skip(Math.Max(0, lines.Length - LogTailLines))).TrimEnd();
            }
            catch (Exception ex)
            {
                return "(журнал не прочитан: " + ex.Message + ")";
            }
        }

        /// <summary>
        /// Видимые окна верхнего уровня: процесс, класс, заголовок, владелец. Через
        /// Win32, а не UI Automation — зависший клиент на запросы автоматизации не
        /// отвечает, а именно такие падения и нужно разбирать.
        /// </summary>
        private static string DescribeWindows()
        {
            var sb = new StringBuilder();
            try
            {
                EnumWindows((hWnd, _) =>
                {
                    if (!IsWindowVisible(hWnd)) return true;

                    var title = new StringBuilder(256);
                    GetWindowText(hWnd, title, title.Capacity);
                    var cls = new StringBuilder(256);
                    GetClassName(hWnd, cls, cls.Capacity);
                    if (title.Length == 0 && GetWindow(hWnd, GW_OWNER) == IntPtr.Zero) return true;

                    GetWindowThreadProcessId(hWnd, out uint pid);
                    string process;
                    try { using var p = Process.GetProcessById((int)pid); process = p.ProcessName; }
                    catch { process = "pid " + pid; }

                    string state = IsHungAppWindow(hWnd) ? ", не отвечает" : "";
                    string owned = GetWindow(hWnd, GW_OWNER) != IntPtr.Zero ? ", с владельцем" : "";
                    string enabled = IsWindowEnabled(hWnd) ? "" : ", отключено";
                    sb.AppendLine($"  [{process}] {cls} «{title}»{owned}{enabled}{state}");
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                sb.AppendLine("  (окна не перечислены: " + ex.Message + ")");
            }
            return sb.ToString();
        }

        private const uint GW_OWNER = 4;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsHungAppWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    }
}
