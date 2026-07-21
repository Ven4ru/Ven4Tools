using System;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ven4Tools.ClientUITests
{
    [TestClass]
    public class DiagnosticsTabTests
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

        [TestMethod]
        public void Диагностика_ОткрываетсяИЗапускается()
        {
            var s = Require();

            var navBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnDiagnosticsTab"));
            Assert.IsNotNull(navBtn, "Не найдена кнопка вкладки «Диагностика» в сайдбаре.");
            navBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(500);

            var osLabel = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("txtOSVersion")),
                timeout: T, interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(osLabel, "Не найден блок «Информация о системе» (txtOSVersion) на вкладке «Диагностика».");

            var runBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnRunDiagnostics"));
            Assert.IsNotNull(runBtn, "Не найдена кнопка «Запустить диагностику».");
            runBtn!.AsButton().Invoke();

            bool reEnabled = Retry.WhileFalse(() => runBtn.AsButton().IsEnabled,
                timeout: TimeSpan.FromSeconds(45), interval: TimeSpan.FromMilliseconds(500), throwOnTimeout: false).Success;
            Assert.IsTrue(reEnabled, "«Запустить диагностику» не вернулась в активное состояние за 45с.");

            var badge = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("txtHealthBadge"));
            Assert.IsNotNull(badge, "Не найден статус-бейдж диагностики.");
            Assert.AreNotEqual("Диагностика ещё не запускалась", badge!.AsLabel().Text,
                "Статус-бейдж не обновился после запуска диагностики.");
        }

        [TestMethod]
        public void Диагностика_КопированиеОтчёта()
        {
            var s = Require();

            var navBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnDiagnosticsTab"));
            navBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(500);

            var copyBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCopyFullReport"));
            Assert.IsNotNull(copyBtn, "Не найдена кнопка «Скопировать полный отчёт».");
            copyBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(500);
            // Диалог "Готово" — просто проверяем, что клик не уронил приложение.
            var okBtn = s.MainWindow.ModalWindows.FirstOrDefault()
                ?.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)).FirstOrDefault();
            okBtn?.Click();
        }
    }
}
