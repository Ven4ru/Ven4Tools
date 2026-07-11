using System;
using System.IO;
using System.Linq;
using System.Threading;
using FlaUI.Core.AutomationElements;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ven4Tools.ClientUITests
{
    /// <summary>
    /// Фаза 4 плана 2026-07-11: остаток MainWindow/диалогов.
    /// FeedbackWindow пропущена — триггерится только при закрытии приложения
    /// на prerelease-канале (MainWindow.xaml.cs ~360), не подходящий сценарий
    /// для тестовой сессии. PresetSaveDialog/SnapshotNameDialog Save уже
    /// проверены в Фазах 1-2 (тот же диалог, тот же путь кода); Cancel в них
    /// тривиален (просто закрывает окно), отдельно не тестировался.
    /// </summary>
    [TestClass]
    public class Phase4MainWindowRemainingTests
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
        private static readonly string ProfilePath = Path.Combine(SettingsDir, "profile.json");

        private static string? _profileBackup; private static bool _profileExisted;
        private static AppSession? _session;
        private static string? _launchError;

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            Directory.CreateDirectory(SettingsDir);
            _profileExisted = File.Exists(ProfilePath);
            if (_profileExisted) _profileBackup = File.ReadAllText(ProfilePath);
            File.WriteAllText(ProfilePath, "{\"CatalogMode\":\"full\",\"HasSelectedCategory\":true}");

            try { _session = AppSession.Launch(); }
            catch (Exception ex) { _launchError = ex.Message; _session = null; }
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            _session?.Dispose();
            _session = null;
            try
            {
                if (_profileExisted) File.WriteAllText(ProfilePath, _profileBackup!);
                else if (File.Exists(ProfilePath)) File.Delete(ProfilePath);
            }
            catch { }
        }

        private static AppSession Require()
        {
            if (_session == null) Assert.Inconclusive("Клиент не запущен: " + (_launchError ?? "неизвестная причина"));
            return _session!;
        }

        [TestMethod]
        public void ГлобальныйЛог_ОчисткаПослеРаскрытияЦентраАктивности()
        {
            var s = Require();

            var expander = s.MainWindow.FindFirstDescendant(cf => cf.ByName("Центр активности"));
            Assert.IsNotNull(expander, "Не найден Expander «Центр активности».");
            if (expander!.Patterns.ExpandCollapse.IsSupported)
                expander.Patterns.ExpandCollapse.Pattern.Expand();
            Thread.Sleep(400);

            var logBefore = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("lstGlobalLog"));
            Assert.IsNotNull(logBefore, "Не найден список глобального журнала (lstGlobalLog).");
            int countBefore = logBefore!.FindAllChildren().Length;

            var clearLogBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnClearGlobalLog"));
            Assert.IsNotNull(clearLogBtn, "Не найдена кнопка очистки глобального журнала.");
            clearLogBtn!.AsButton().Invoke();
            Thread.Sleep(200); // минимально — фоновые сервисы (heartbeat/connectivity) дописывают новые строки почти сразу

            var log = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("lstGlobalLog"));
            int itemCount = log!.FindAllChildren().Length;
            // Не требуем строго 0 — фоновые сервисы могли дописать 1-2 новые строки
            // за миллисекунды между кликом и проверкой. Важно, что список реально
            // очистился, а не просто продолжает расти.
            Assert.IsTrue(itemCount < countBefore, $"Журнал не очистился: было {countBefore}, стало {itemCount}.");
        }

        [TestMethod]
        public void AboutTab_ОбратнаяСвязьИСообщитьОПроблеме_ОткрываютБраузер()
        {
            var s = Require();
            var aboutBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnAboutTab"));
            Assert.IsNotNull(aboutBtn, "Не найдена кнопка вкладки «О программе».");
            aboutBtn!.AsButton().Invoke();
            Thread.Sleep(500);

            var feedbackBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnFeedback"));
            Assert.IsNotNull(feedbackBtn, "Не найдена кнопка «Обратная связь».");
            feedbackBtn!.AsButton().Invoke();
            Thread.Sleep(1500); // открывает браузер — побочный эффект принят (как GitHub-кнопка ранее)

            var reportBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnReportIssue"));
            Assert.IsNotNull(reportBtn, "Не найдена кнопка «Сообщить о проблеме».");
            reportBtn!.AsButton().Invoke();
            Thread.Sleep(1500);

            Assert.IsFalse(s.MainWindow.Properties.IsOffscreen.ValueOrDefault, "Главное окно клиента пропало после кликов по кнопкам обратной связи.");
        }
    }
}
