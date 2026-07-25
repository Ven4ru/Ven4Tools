using System;
using System.IO;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ven4Tools.ClientUITests
{
    /// <summary>
    /// Рантайм-проверка вкладки «Бенчмарк».
    ///
    /// Тест реально прогоняет замер на самом быстром профиле: это единственный способ
    /// убедиться, что небуферизованный ввод-вывод и удаление временного файла работают
    /// в живом приложении, а не только в юнит-тестах. Прогон занимает около минуты и
    /// записывает на диск временный файл в 1 ГиБ, который удаляется по завершении.
    /// </summary>
    [TestClass]
    public class BenchmarkTabTests
    {
        private static AppSession? _session;
        private static string? _launchError;
        private static readonly TimeSpan T = TimeSpan.FromSeconds(15);

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            try { _session = AppSession.Launch(); }
            catch (Exception ex) { _launchError = ex.Message; _session = null; }
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            _session?.Dispose();
            _session = null;
        }

        private static AppSession Require()
        {
            if (_session == null) Assert.Inconclusive("Клиент не запущен: " + (_launchError ?? "неизвестная причина"));
            return _session!;
        }

        private static AutomationElement OpenTab()
        {
            var s = Require();

            var navBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnBenchmarkTab"));
            Assert.IsNotNull(navBtn, "Не найдена кнопка вкладки «Бенчмарк» в сайдбаре.");
            navBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(700);

            var disks = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("cmbDisks")),
                timeout: T, interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(disks, "Не найден список накопителей на вкладке «Бенчмарк».");
            return s.MainWindow;
        }

        [TestMethod]
        public void Бенчмарк_СписокНакопителейЗаполняется()
        {
            var window = OpenTab();

            var disks = window.FindFirstDescendant(cf => cf.ByAutomationId("cmbDisks"))!.AsComboBox();
            bool filled = Retry.WhileFalse(() => disks.Items.Length > 0,
                timeout: T, interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Success;
            Assert.IsTrue(filled, "Список накопителей остался пустым.");

            var connection = window.FindFirstDescendant(cf => cf.ByAutomationId("txtConnection"))!.AsLabel();
            Assert.AreNotEqual("—", connection.Text, "Сведения о подключении накопителя не заполнились.");

            var volumes = window.FindFirstDescendant(cf => cf.ByAutomationId("cmbVolumes"))!.AsComboBox();
            Assert.IsTrue(volumes.Items.Length > 0, "Не предложено ни одного тома для теста.");
        }

        [TestMethod]
        public void Бенчмарк_ПотолокИнтерфейсаЛибоЧисло_ЛибоЧестноеНеизвестно()
        {
            var window = OpenTab();

            var ceiling = window.FindFirstDescendant(cf => cf.ByAutomationId("txtCeiling"))!.AsLabel();
            string text = ceiling.Text ?? "";

            bool honest = text.Contains("МБ/с") || text.Contains("неизвестно");
            Assert.IsTrue(honest,
                "Потолок интерфейса должен быть либо конкретным числом, либо честным «неизвестно». Получено: " + text);
        }

        /// <summary>
        /// Выбор накопителя перевыставляет и том, из-за чего пересбор предупреждений
        /// запускался дважды внахлёст и панель заполнялась дублями. Тест закрывает регресс.
        /// </summary>
        [TestMethod]
        public void Бенчмарк_ПредупрежденияНеДублируются()
        {
            var window = OpenTab();
            System.Threading.Thread.Sleep(1500);

            // Панели WPF в дерево автоматизации не попадают, поэтому ищем по маркеру списка:
            // до запуска теста маркированы только предупреждения, выводы ещё пусты.
            var texts = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                              .Select(e => e.Name ?? "")
                              .Where(t => t.StartsWith("•"))
                              .ToList();

            if (texts.Count == 0) return; // предупреждений нет — дублировать нечего

            Assert.AreEqual(texts.Count, texts.Distinct().Count(),
                "Список предупреждений содержит повторы: " + string.Join(" | ", texts));
        }

        [TestMethod]
        public void Бенчмарк_ПрогонНаБыстромПрофилеДаётРезультаты()
        {
            var window = OpenTab();

            // Быстрый профиль — один проход, чтобы тест уложился примерно в минуту.
            var profile = window.FindFirstDescendant(cf => cf.ByAutomationId("cmbProfile"))!.AsComboBox();
            profile.Select(0);
            System.Threading.Thread.Sleep(300);

            var runBtn = window.FindFirstDescendant(cf => cf.ByAutomationId("btnRunBenchmark"))!.AsButton();

            // Кнопка включается после опроса тома, а тот обращается к WMI и занимает секунды.
            bool ready = Retry.WhileFalse(() => runBtn.IsEnabled,
                timeout: TimeSpan.FromSeconds(30), interval: TimeSpan.FromMilliseconds(500), throwOnTimeout: false).Success;
            Assert.IsTrue(ready, "Кнопка запуска теста так и не стала доступной — возможно, не хватает места на томе.");

            SaveScreenshot(window, "benchmark-before-run");

            runBtn.Invoke();

            // Сначала дожидаемся, что прогон действительно начался: сразу после нажатия
            // кнопка ещё называется по-старому, и проверка на завершение прошла бы вхолостую.
            bool started = Retry.WhileFalse(
                () => (runBtn.Name ?? "").Contains("Остановить"),
                timeout: TimeSpan.FromSeconds(30), interval: TimeSpan.FromMilliseconds(500), throwOnTimeout: false).Success;
            Assert.IsTrue(started, "Прогон не начался: кнопка не переключилась в режим остановки.");

            bool finished = Retry.WhileFalse(
                () => (runBtn.Name ?? "").Contains("Запустить"),
                timeout: TimeSpan.FromMinutes(4), interval: TimeSpan.FromSeconds(2), throwOnTimeout: false).Success;
            Assert.IsTrue(finished, "Тест не завершился за отведённое время.");

            var firstCell = window.FindFirstDescendant(cf => cf.ByAutomationId("txtP0Read"))!.AsLabel();
            Assert.AreNotEqual("—", firstCell.Text, "Результат последовательного чтения не заполнился.");
            StringAssert.Contains(firstCell.Text, "МБ/с", "Ожидалась скорость в МБ/с.");

            var copyBtn = window.FindFirstDescendant(cf => cf.ByAutomationId("btnCopyReport"))!.AsButton();
            Assert.IsTrue(copyBtn.IsEnabled, "Кнопка копирования отчёта осталась заблокированной.");

            // Прокручиваем к таблице результатов — так снимок годится для разбора вёрстки.
            try
            {
                FlaUI.Core.Input.Mouse.MoveTo(window.BoundingRectangle.X + window.BoundingRectangle.Width / 2,
                                              window.BoundingRectangle.Y + window.BoundingRectangle.Height / 2);
                for (int i = 0; i < 6; i++)
                {
                    FlaUI.Core.Input.Mouse.Scroll(-10);
                    System.Threading.Thread.Sleep(150);
                }
                System.Threading.Thread.Sleep(500);
            }
            catch
            {
                // Прокрутка нужна только для наглядности снимка.
            }

            SaveScreenshot(window, "benchmark-after-run");

            // Временный файл обязан быть удалён после прогона на любом томе.
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                string path = Path.Combine(drive.RootDirectory.FullName, "Ven4Tools_benchmark.tmp");
                Assert.IsFalse(File.Exists(path), "Временный файл теста остался на диске: " + path);
            }
        }

        private static void SaveScreenshot(AutomationElement window, string name)
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "Ven4Tools_UITests");
                Directory.CreateDirectory(dir);
                Capture.Element(window).ToFile(Path.Combine(dir, name + ".png"));
            }
            catch
            {
                // Снимок экрана — вспомогательная диагностика, его отсутствие не должно валить тест.
            }
        }
    }
}
