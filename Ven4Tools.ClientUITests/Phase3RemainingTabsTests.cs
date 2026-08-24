using System;
using System.IO;
using System.Linq;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ven4Tools.ClientUITests
{
    /// <summary>
    /// Фаза 3 плана 2026-07-11: безопасный остаток Network/Installed/Office/
    /// Activation/Debloater — самых рискованных вкладок проекта. Кнопки,
    /// реально устанавливающие/удаляющие/меняющие систему (winget upgrade --all,
    /// применение твиков Debloater, установка Office, активация) сюда не входят —
    /// они код-ревью-подтверждены отдельно, живой клик по ним не выполняется.
    /// </summary>
    [TestClass]
    public class Phase3RemainingTabsTests
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
        private static readonly string ProfilePath = Path.Combine(SettingsDir, "profile.json");
        private static readonly string HistoryPath = Path.Combine(SettingsDir, "install_history.json");

        private static string? _profileBackup; private static bool _profileExisted;
        private static string? _historyBackup; private static bool _historyExisted;
        private static AppSession? _session;
        private static string? _launchError;
        private static readonly TimeSpan T = TimeSpan.FromSeconds(15);

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            Directory.CreateDirectory(SettingsDir);
            _profileExisted = File.Exists(ProfilePath);
            if (_profileExisted) _profileBackup = File.ReadAllText(ProfilePath);
            File.WriteAllText(ProfilePath, "{\"CatalogMode\":\"full\",\"HasSelectedCategory\":true}");

            _historyExisted = File.Exists(HistoryPath);
            if (_historyExisted) _historyBackup = File.ReadAllText(HistoryPath);

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

                if (_historyExisted) File.WriteAllText(HistoryPath, _historyBackup!);
                else if (File.Exists(HistoryPath)) File.Delete(HistoryPath);
            }
            catch { }
        }

        private static AppSession Require()
        {
            if (_session == null) Assert.Inconclusive("Клиент не запущен: " + (_launchError ?? "неизвестная причина"));
            return _session!;
        }

        private static void ClickAndWaitReEnabled(AppSession s, Button btn, int timeoutSec = 30)
        {
            btn.Invoke();
            Retry.WhileFalse(() => btn.IsEnabled, timeout: TimeSpan.FromSeconds(timeoutSec),
                interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false);
        }

        /// <summary>
        /// Ищет модальный диалог очистки истории (заголовок «Очистка»). Смотрим
        /// и среди модальных детей главного окна, и среди верхнеуровневых окон
        /// рабочего стола: MessageBox не всегда числится дочерним.
        /// </summary>
        private static Window? FindClearDialog(AppSession s, TimeSpan timeout)
        {
            return Retry.WhileNull(
                () => s.MainWindow.ModalWindows.FirstOrDefault(w => w.Title.Contains("Очистка"))
                      ?? s.Automation.GetDesktop()
                          .FindAllChildren(cf => cf.ByControlType(ControlType.Window))
                          .FirstOrDefault(w => w.Name.Contains("Очистка"))
                          ?.AsWindow(),
                timeout: timeout,
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false).Result;
        }

        /// <summary>
        /// Закрывает диалог очистки, если он остался открытым: иначе повиснет весь
        /// класс тестов. Вызывается из finally, поэтому тело целиком обёрнуто в
        /// try/catch: любое исключение отсюда затёрло бы настоящую причину падения
        /// теста. Кнопку берём по подписи «Да»/«Yes», а при её отсутствии — первую
        /// доступную кнопку: отключённая кнопка закрытия заголовка (X) у
        /// MessageBoxButton.YesNo не должна попасть под Invoke().
        /// </summary>
        private static void CloseClearDialogIfAny(AppSession s)
        {
            try
            {
                var dlg = FindClearDialog(s, TimeSpan.FromSeconds(1));
                if (dlg == null) return;
                var buttons = dlg.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
                var btn = buttons.FirstOrDefault(b => b.Name.Contains("Да") || b.Name.Contains("Yes"))
                          ?? buttons.FirstOrDefault(b => b.IsEnabled);
                btn?.AsButton().Invoke();
                Thread.Sleep(400);
            }
            catch { }
        }

        [TestMethod]
        public void NetworkTab_ОстальныеДиагностическиеКнопки()
        {
            var s = Require();
            var netBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnNetworkTab"));
            Assert.IsNotNull(netBtn, "Не найдена кнопка вкладки «Сеть».");
            netBtn!.AsButton().Invoke();
            Thread.Sleep(500);

            var refreshAdapters = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnRefreshAdapters"));
            Assert.IsNotNull(refreshAdapters, "Не найдена кнопка «Обновить» (адаптеры).");
            refreshAdapters!.AsButton().Invoke();
            Thread.Sleep(1000);

            var ping = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnPing"));
            Assert.IsNotNull(ping, "Не найдена кнопка «Пинговать».");
            ClickAndWaitReEnabled(s, ping!.AsButton(), 20);

            var checkServices = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCheckServices"));
            Assert.IsNotNull(checkServices, "Не найдена кнопка «Проверить сервисы».");
            ClickAndWaitReEnabled(s, checkServices!.AsButton(), 20);

            var getIp = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnGetIp"));
            Assert.IsNotNull(getIp, "Не найдена кнопка «Определить» (IP).");
            ClickAndWaitReEnabled(s, getIp!.AsButton(), 20);

            var checkDns = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCheckDns"));
            Assert.IsNotNull(checkDns, "Не найдена кнопка «Проверить DNS».");
            ClickAndWaitReEnabled(s, checkDns!.AsButton(), 20);

            // btnResetNetwork НЕ кликаем — реально меняет сетевую конфигурацию (netsh
            // winsock reset), это риск-код-ревью, не безопасная кнопка.
        }

        [TestMethod]
        public void InstalledTab_ПроверитьОбновления()
        {
            var s = Require();
            var installedBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnInstalledTab"));
            Assert.IsNotNull(installedBtn, "Не найдена кнопка вкладки «Установленные».");
            installedBtn!.AsButton().Invoke();
            Thread.Sleep(1500);

            var refreshBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnRefresh"));
            Assert.IsNotNull(refreshBtn, "Не найдена кнопка «Проверить обновления» (Установленные).");
            ClickAndWaitReEnabled(s, refreshBtn!.AsButton(), 60);
        }

        [TestMethod]
        public void OfficeTab_ОтменаИПереходКАктивации()
        {
            var s = Require();
            var officeBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnOfficeTab"));
            Assert.IsNotNull(officeBtn, "Не найдена кнопка вкладки «Office».");
            officeBtn!.AsButton().Invoke();
            Thread.Sleep(500);

            // btnCancelOffice активна только во время реальной установки — вне
            // операции она задизейблена, клик по ней вживую не имеет смысла.
            var cancelBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCancelOffice"));
            Assert.IsNotNull(cancelBtn, "Не найдена кнопка «Отмена» (Office).");
            Assert.IsFalse(cancelBtn!.AsButton().IsEnabled, "btnCancelOffice ожидалась задизейбленной вне активной установки.");

            var goActivationBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnGoActivation"));
            Assert.IsNotNull(goActivationBtn, "Не найдена кнопка «Активация →».");
            goActivationBtn!.AsButton().Invoke();
            Thread.Sleep(500);

            var activationTab = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCheckStatus"));
            Assert.IsNotNull(activationTab, "Переход «Активация →» не привёл на вкладку ActivationTab (btnCheckStatus не найден).");
        }

        [TestMethod]
        public void ActivationTab_ПроверитьСтатус()
        {
            var s = Require();
            var activationBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnActivationTab"));
            Assert.IsNotNull(activationBtn, "Не найдена кнопка вкладки «Лицензия».");
            activationBtn!.AsButton().Invoke();
            Thread.Sleep(500);

            var checkStatusBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCheckStatus"));
            Assert.IsNotNull(checkStatusBtn, "Не найдена кнопка «Проверить статус».");
            ClickAndWaitReEnabled(s, checkStatusBtn!.AsButton(), 30);
        }

        [TestMethod]
        public void DebloaterTab_ВыбратьВсеИСброс()
        {
            var s = Require();
            var debloaterBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnDebloaterTab"));
            Assert.IsNotNull(debloaterBtn, "Не найдена кнопка вкладки «Очистка».");
            debloaterBtn!.AsButton().Invoke();
            Thread.Sleep(500);

            var selectAll = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnDebloatSelectAll"));
            Assert.IsNotNull(selectAll, "Не найдена кнопка «Все» (Очистка).");
            selectAll!.AsButton().Invoke();
            Thread.Sleep(300);

            var selectNone = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnDebloatSelectNone"));
            Assert.IsNotNull(selectNone, "Не найдена кнопка «Сброс» (Очистка).");
            selectNone!.AsButton().Invoke();
            Thread.Sleep(300);

            // btnApplyDebloat НЕ кликаем — реально удаляет Appx-пакеты/трогает
            // реестр и службы, это риск-код-ревью, не безопасная кнопка.
        }

        [TestMethod]
        public void HistoryTab_ПоискФильтрОчистка()
        {
            var s = Require();
            var historyBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnHistoryTab"));
            Assert.IsNotNull(historyBtn, "Не найдена кнопка вкладки «История».");
            historyBtn!.AsButton().Invoke();
            Thread.Sleep(500);

            var search = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("txtHistorySearch"));
            Assert.IsNotNull(search, "Не найдено поле поиска (История).");
            var count = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("txtHistoryCount"));
            Assert.IsNotNull(count, "Не найден счётчик записей (История).");

            // Поиск заведомо несуществующего текста должен обнулить список —
            // единственное утверждение, не зависящее от того, есть ли реальная
            // история на тестовой машине.
            search!.AsTextBox().Text = "несуществующее-приложение-zzz-12345";
            Retry.WhileFalse(() => count!.AsLabel().Text == "0",
                timeout: TimeSpan.FromSeconds(5), interval: TimeSpan.FromMilliseconds(200),
                throwOnTimeout: false);
            Assert.AreEqual("0", count!.AsLabel().Text, "Поиск по несуществующему тексту должен обнулить счётчик.");

            search.AsTextBox().Text = "";
            Thread.Sleep(300);

            var successOnly = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("togSuccessOnly"));
            Assert.IsNotNull(successOnly, "Не найден переключатель «Успешные» (История).");
            bool? successWas = successOnly!.AsToggleButton().IsToggled;
            successOnly.AsToggleButton().Toggle();
            Thread.Sleep(300);

            var failOnly = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("togFailOnly"));
            Assert.IsNotNull(failOnly, "Не найден переключатель «Неудачные» (История).");
            bool? failWas = failOnly!.AsToggleButton().IsToggled;
            failOnly.AsToggleButton().Toggle();
            Thread.Sleep(300);

            // Возвращаем оба переключателя ровно в то состояние, в котором они
            // были до дымовой проверки фильтров — это гигиена теста, чтобы
            // вкладка осталась чистой для любого будущего теста. На счётчик
            // после очистки это не влияет: ApplyFilter применяет ветку
            // «только успешные» лишь при !FailOnly и наоборот, поэтому при двух
            // включённых переключателях не работает ни один из фильтров.
            if (successOnly.AsToggleButton().IsToggled != successWas) successOnly.AsToggleButton().Toggle();
            if (failOnly.AsToggleButton().IsToggled != failWas) failOnly.AsToggleButton().Toggle();
            Thread.Sleep(300);

            var clearBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnClearHistory"));
            Assert.IsNotNull(clearBtn, "Не найдена кнопка «Очистить» (История).");
            try
            {
                clearBtn!.AsButton().Invoke();

                var confirmWindow = FindClearDialog(s, TimeSpan.FromSeconds(10));
                Assert.IsNotNull(confirmWindow, "Не найден диалог подтверждения очистки истории.");
                // Кнопку ищем по подписи, а не «первую по типу»: системный
                // MessageBox (#32770) отдаёт в дереве автоматизации ещё и кнопку
                // закрытия заголовка (X), которая для MessageBoxButton.YesNo
                // отключена, — попав на неё первой, Invoke() бросил бы
                // ElementNotEnabledException. Машины и тесты проекта
                // русскоязычные, «Yes» оставлен запасным вариантом.
                var yesBtn = confirmWindow!.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .FirstOrDefault(b => b.Name.Contains("Да") || b.Name.Contains("Yes"));
                Assert.IsNotNull(yesBtn, "Не найдена кнопка подтверждения в диалоге очистки.");
                yesBtn!.AsButton().Invoke();

                Retry.WhileFalse(() => count.AsLabel().Text == "0",
                    timeout: TimeSpan.FromSeconds(5), interval: TimeSpan.FromMilliseconds(200),
                    throwOnTimeout: false);
                Assert.AreEqual("0", count.AsLabel().Text, "После подтверждённой очистки счётчик истории должен быть 0.");
            }
            finally
            {
                // Незакрытая модальная карточка блокирует ввод во всём классе
                // тестов, поэтому закрываем её даже если утверждение выше упало.
                CloseClearDialogIfAny(s);
            }

            // btnReinstall (🔄 на строках) НЕ кликаем — реально ставит приложение
            // через сеть, это риск-код-ревью, не безопасная кнопка. После очистки
            // выше список и так пуст — строк с этой кнопкой не осталось.
        }
    }
}
