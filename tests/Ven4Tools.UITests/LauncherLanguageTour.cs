using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using System.Diagnostics;
using Xunit;

namespace Ven4Tools.UITests;

/// <summary>
/// Снимки окон лаунчера на выбранном языке — проверка вёрстки перевода: не обрезан ли
/// текст и не осталось ли русских строк. Это не проверка в обычном смысле, поэтому без
/// VEN4TOOLS_LANGUAGE_TOUR=1 она ничего не делает. Язык берётся из VEN4TOOLS_LANG, снимки
/// складываются в %TEMP%\ven4tools_screenshots\launcher-&lt;язык&gt;-&lt;вид окна&gt;.
/// Окна и кнопки ищутся по идентификаторам, а не по подписям.
/// </summary>
public sealed class LauncherLanguageTour
{
    [Fact]
    [Trait("Category", "LanguageTour")]
    public void CaptureLauncherWindows()
    {
        if (Environment.GetEnvironmentVariable("VEN4TOOLS_LANGUAGE_TOUR") != "1") return;

        string language = Environment.GetEnvironmentVariable("VEN4TOOLS_LANG") ?? "ru";
        foreach (string shell in new[] { "modern", "classic" })
        {
            string output = Path.Combine(Path.GetTempPath(), "ven4tools_screenshots", $"launcher-{language}-{shell}");
            Directory.CreateDirectory(output);
            string testRoot = Path.Combine(Path.GetTempPath(), $"Ven4Tools.UI.Tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(testRoot);
            File.WriteAllText(Path.Combine(testRoot, "launcher_settings.json"), "{\"UiMode\":\"" + shell + "\"}");

            var startInfo = new ProcessStartInfo(LauncherTestEnvironment.FindLauncher());
            startInfo.Environment["VEN4TOOLS_UI_TEST"] = "1";
            startInfo.Environment["VEN4TOOLS_UI_TEST_ROOT"] = testRoot;
            startInfo.Environment["VEN4TOOLS_LANG"] = language;

            using var automation = new UIA3Automation();
            using var application = Application.Launch(startInfo);
            try
            {
                Window window = Retry.WhileNull(
                    () => application.GetMainWindow(automation),
                    timeout: TimeSpan.FromSeconds(20), interval: TimeSpan.FromMilliseconds(250)).Result
                    ?? throw new InvalidOperationException("Главное окно лаунчера не появилось.");
                Retry.WhileFalse(
                    () => window.FindFirstDescendant(c => c.ByAutomationId("btnSelectFolder"))?.IsEnabled == true,
                    timeout: TimeSpan.FromSeconds(20), interval: TimeSpan.FromMilliseconds(250));
                Thread.Sleep(TimeSpan.FromSeconds(3));   // первая проверка версий успевает дописать журнал
                Save(window, Path.Combine(output, "01-main.png"));

                // Журнал лаунчера: развёрнутая панель с ходом работы.
                var log = window.FindFirstDescendant(c => c.ByAutomationId("logExpander"));
                if (log?.Patterns.ExpandCollapse.IsSupported == true)
                {
                    log.Patterns.ExpandCollapse.Pattern.Expand();
                    Thread.Sleep(700);
                    Save(window, Path.Combine(output, "02-main-log.png"));
                }

                window.FindFirstDescendant(c => c.ByAutomationId("btnChangelog"))?.AsButton().Invoke();
                Thread.Sleep(1200);
                Save(window, Path.Combine(output, "03-details.png"));
                window.FindFirstDescendant(c => c.ByAutomationId("btnCloseDetails"))?.AsButton().Invoke();
                Thread.Sleep(500);

                window.FindFirstDescendant(c => c.ByAutomationId("btnOpenSettings"))?.AsButton().Invoke();
                // Окно настроек принадлежит главному и в дереве автоматизации лежит внутри
                // него: ищем его как дочернее окно, а не среди окон верхнего уровня.
                Window? settings = Retry.WhileNull(
                    () => window.FindAllDescendants(c => c.ByControlType(FlaUI.Core.Definitions.ControlType.Window))
                        .FirstOrDefault(w => w.FindFirstDescendant(c => c.ByAutomationId("cmbDownloadSource")) != null)
                        ?.AsWindow(),
                    timeout: TimeSpan.FromSeconds(8), interval: TimeSpan.FromMilliseconds(250)).Result;
                if (settings != null)
                {
                    Thread.Sleep(600);
                    Save(settings, Path.Combine(output, "04-settings.png"));
                    settings.FindFirstDescendant(c => c.ByAutomationId("btnCloseSettings"))?.AsButton().Invoke();
                }
            }
            finally
            {
                application.Close();
                if (!application.HasExited) application.Kill();
                try { Directory.Delete(testRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void Save(Window window, string path)
    {
        IntPtr handle = new(window.Properties.NativeWindowHandle.Value);
        WindowCapture.Capture(handle).SavePng(path);
    }
}
