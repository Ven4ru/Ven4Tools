using System;
using System.IO;
using System.Linq;
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
                    var btn = UiNav.Find(s, navBtnId);
                    Assert.IsNotNull(btn, $"Не найдена кнопка навигации {navBtnId}.");
                    btn!.AsButton().Invoke();
                    Thread.Sleep(waitMs);

                    // Первый заход на Windows Update показывает онбординг-диалог выбора
                    // режима, а после его закрытия сам запускает проверку обновлений —
                    // которая на машине с остановленной службой wuauserv упирается в
                    // системный запрос "запустить службу?". Не даём его подтвердить:
                    // для диалогов Да/Нет жмём "Нет" (не трогаем реальную службу),
                    // для остальных (онбординг) — дефолтную кнопку OK/Готово.
                    for (int i = 0; i < 3; i++)
                    {
                        var modal = s.MainWindow.ModalWindows.Length > 0 ? s.MainWindow.ModalWindows[0] : null;
                        if (modal == null) break;

                        var noBtn = modal.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button))
                            .FirstOrDefault(b => (b.Name ?? "") == "Нет" || (b.Name ?? "") == "No");
                        if (noBtn != null) { noBtn.Click(); Thread.Sleep(500); continue; }

                        var okBtn = modal.FindFirstDescendant(cf => cf.ByAutomationId("btnOk"))
                                    ?? modal.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button)).FirstOrDefault();
                        okBtn?.AsButton().Invoke();
                        Thread.Sleep(500);
                    }
                    Thread.Sleep(500);

                    using var capture = s.MainWindow.Capture();
                    string path = Path.Combine(OutDir, fileName);
                    capture.Save(path);
                    Console.WriteLine($"Сохранено: {path}");
                }

                Shot("btnCatalogTab", "catalog.png", 2500);
                Shot("btnInstalledTab", "installed.png", 2000);
                Shot("btnSystemTab", "system.png");
                Shot("btnWindowsUpdateTab", "windowsupdate.png", 2000);
                Shot("btnOfficeTab", "office.png");
                Shot("btnDebloaterTab", "debloater.png");
                Shot("btnAboutTab", "about.png");
            }
            finally
            {
                session?.Dispose();
            }
        }

        private static readonly string[] TourSections =
        {
            "btnOverviewTab", "btnCatalogTab", "btnInstalledTab", "btnSystemTab", "btnDiagnosticsTab",
            "btnBenchmarkTab", "btnWindowsUpdateTab", "btnOfficeTab", "btnDebloaterTab",
            "btnNetworkTab", "btnHistoryTab", "btnAboutTab",
        };

        /// <summary>
        /// Обход всех разделов клиента в обеих оболочках со снимком каждого экрана: раздел,
        /// его внутренние вкладки и прокрутка длинных страниц. Язык берётся из переменной
        /// окружения VEN4TOOLS_LANG — так проверяется вёрстка на другом языке: не обрезан
        /// ли текст и не осталось ли русских строк. Разделы ищутся по идентификаторам,
        /// а не по подписям, поэтому обход не зависит от языка.
        /// </summary>
        [TestMethod]
        [TestCategory("LanguageTour")]
        public void СделатьСнимкиВсехРазделов()
        {
            string language = Environment.GetEnvironmentVariable("VEN4TOOLS_LANG") ?? "ru";
            foreach (bool modern in new[] { true, false })
            {
                string dir = Path.Combine(OutDir, $"tour-{language}-{(modern ? "modern" : "classic")}");
                Directory.CreateDirectory(dir);

                AppSession? session = null;
                try { session = AppSession.Launch(modern); }
                catch (Exception ex) { Assert.Inconclusive("Клиент не запущен: " + ex.Message); }

                var s = session!;
                try
                {
                    s.MainWindow.SetForeground();
                    s.WaitForAvailabilityChecked();
                    int number = 0;

                    void Save(string name)
                    {
                        using var capture = s.MainWindow.Capture();
                        capture.Save(Path.Combine(dir, $"{++number:00}-{name}.png"));
                    }

                    foreach (string sectionId in TourSections)
                    {
                        var button = UiNav.Find(s, sectionId);
                        if (button == null) continue;   // раздела нет в этой оболочке или без сети
                        button.AsButton().Invoke();
                        Thread.Sleep(1800);
                        DismissDialogs(s);

                        string section = sectionId.Replace("btn", "").Replace("Tab", "");
                        Save(section);
                        ScrollAndSave(s, name => Save($"{section}-{name}"));

                        var innerTabs = s.MainWindow.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.TabItem));
                        for (int i = 1; i < innerTabs.Length; i++)
                        {
                            try { innerTabs[i].AsTabItem().Select(); }
                            catch { continue; }
                            Thread.Sleep(900);
                            DismissDialogs(s);
                            Save($"{section}-tab{i}");
                            ScrollAndSave(s, name => Save($"{section}-tab{i}-{name}"));
                        }
                    }
                }
                finally
                {
                    s.Dispose();
                }
            }
        }

        // Диалоги, которые разделы показывают при первом входе: вопрос «Да/Нет» закрывается
        // отказом (ничего в системе не меняем), остальные — кнопкой по умолчанию.
        private static void DismissDialogs(AppSession s)
        {
            for (int i = 0; i < 3; i++)
            {
                var modal = s.MainWindow.ModalWindows.Length > 0 ? s.MainWindow.ModalWindows[0] : null;
                if (modal == null) return;

                var buttons = modal.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button));
                var no = buttons.FirstOrDefault(b => (b.Name ?? "") is "Нет" or "No");
                if (no != null) no.Click();
                else (modal.FindFirstDescendant(cf => cf.ByAutomationId("btnOk")) ?? buttons.FirstOrDefault())?.AsButton().Invoke();
                Thread.Sleep(500);
            }
        }

        // Длинная страница: листаем самую большую прокручиваемую область и снимаем каждый экран.
        private static void ScrollAndSave(AppSession s, Action<string> save)
        {
            AutomationElement? pane = null;
            double best = 0;
            foreach (var candidate in s.MainWindow.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Pane)))
            {
                try
                {
                    if (!candidate.Patterns.Scroll.IsSupported || !candidate.Patterns.Scroll.Pattern.VerticallyScrollable.Value) continue;
                    double area = candidate.BoundingRectangle.Width * candidate.BoundingRectangle.Height;
                    if (area > best) { best = area; pane = candidate; }
                }
                catch { }
            }
            if (pane == null) return;

            var scroll = pane.Patterns.Scroll.Pattern;
            for (int page = 1; page <= 8; page++)
            {
                try
                {
                    if (scroll.VerticalScrollPercent.Value >= 99.5) break;
                    scroll.Scroll(FlaUI.Core.Definitions.ScrollAmount.NoAmount, FlaUI.Core.Definitions.ScrollAmount.LargeIncrement);
                }
                catch { break; }
                Thread.Sleep(500);
                save($"page{page + 1}");
            }
            try { scroll.SetScrollPercent(-1, 0); } catch { }
        }
    }
}
