using System;
using System.IO;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ven4Tools.ClientUITests
{
    /// <summary>
    /// Утилита для генерации скриншотов клиента под сайт (assets/images/screenshots/).
    /// Не тест в обычном смысле — запускается вручную через dotnet test --filter,
    /// сохраняет реальные снимки окна для каждой вкладки.
    /// </summary>
    [TestClass]
    public class ScreenshotGeneration
    {
        private static readonly string OutDir = Path.Combine(
            Path.GetTempPath(), "ven4tools_screenshots");

        [TestMethod]
        public void СделатьСкриншотыКлючевыхВкладок()
        {
            Directory.CreateDirectory(OutDir);

            AppSession? session = null;
            try { session = AppSession.Launch(); }
            catch (Exception ex) { Assert.Inconclusive("Клиент не запущен: " + ex.Message); }

            var s = session!;
            try
            {
                s.MainWindow.SetForeground();
                Thread.Sleep(500);

                void Shot(string navBtnId, string fileName, int waitMs = 1500)
                {
                    var btn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(navBtnId));
                    Assert.IsNotNull(btn, $"Не найдена кнопка навигации {navBtnId}.");
                    btn!.AsButton().Invoke();
                    Thread.Sleep(waitMs);

                    using var capture = s.MainWindow.Capture();
                    string path = Path.Combine(OutDir, fileName);
                    capture.Save(path);
                    Console.WriteLine($"Сохранено: {path}");
                }

                Shot("btnCatalogTab", "catalog.png", 2500);
                Shot("btnInstalledTab", "installed.png", 2000);
                Shot("btnSystemTab", "system.png");
                Shot("btnOfficeTab", "office.png");
                Shot("btnDebloaterTab", "debloater.png");
                Shot("btnAboutTab", "about.png");
            }
            finally
            {
                session?.Dispose();
            }
        }
    }
}
