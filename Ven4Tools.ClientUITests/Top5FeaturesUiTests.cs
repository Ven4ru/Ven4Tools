using System;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ven4Tools.ClientUITests
{
    /// <summary>
    /// Рантайм-проверка трёх клиентских доработок топ-5 (2026-07-29): поиск по
    /// описанию/идентификаторам источников, взаимные переходы «Диагностика» ↔
    /// «Обновления Windows», блок неуспешных установок в каталоге. Юнит-тесты
    /// покрывают саму логику (CatalogRowSearchTests, InstallFailureReportTests,
    /// FailedInstallViewModelTests) — здесь проверяется только то, что не
    /// проверяется юнит-тестами: реальная разметка WPF и жива ли навигация в
    /// собранном клиенте.
    ///
    /// Позитивный сценарий блока «Не установлено» (реальная неудачная установка)
    /// намеренно не автоматизирован — детерминированно спровоцировать сетевую
    /// ошибку/несовпадение SHA256 в живой среде небезопасно и нестабильно;
    /// проверяется только то, что панель по умолчанию скрыта, пока неудач нет.
    /// Так же и с кнопками перехода Диагностика↔Windows Update: они появляются
    /// только на реальных проблемных диагностических исходах, которые в тестовой
    /// среде не воспроизводимы предсказуемо — проверяется скрытое состояние
    /// «по умолчанию, до запуска проверки».
    /// </summary>
    [TestClass]
    public class Top5FeaturesUiTests
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

        private static AutomationElement NavigateTo(AppSession s, string navButtonAutomationId)
        {
            var btn = UiNav.Find(s, navButtonAutomationId);
            Assert.IsNotNull(btn, $"Не найдена кнопка навигации «{navButtonAutomationId}» в сайдбаре.");
            btn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(700);
            return s.MainWindow;
        }

        // ─────────────────────────────────────────────────────────────────────
        // 1. Поиск по описанию/идентификаторам источников
        // ─────────────────────────────────────────────────────────────────────

        [TestMethod]
        public void Поиск_ПоФрагментуОписания_НаходитПриложение()
        {
            var s = Require();
            NavigateTo(s, "btnCatalogTab");

            var search = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("txtSearch")),
                timeout: T, interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(search, "Не найдено поле поиска каталога (txtSearch).");

            // Даём фоновой загрузке каталога (доступность/версии/статус установки)
            // отработать первую секунду, прежде чем гонять с ней фильтрацию —
            // тот же приём, что в SearchSuggestionsUiTests.
            System.Threading.Thread.Sleep(1000);

            // «Markdown» встречается в описании Obsidian («База знаний и заметки
            // в Markdown с двусвязными ссылками»), но не в его DisplayName —
            // до доработки топ-5 (2026-07-29) такой запрос не находил бы приложение.
            search!.Click();
            search.AsTextBox().Enter("Markdown");
            System.Threading.Thread.Sleep(800);

            bool foundByDescription = Retry.WhileFalse(
                () => s.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .Any(el => (el.Name ?? "").Contains("Obsidian")),
                timeout: T, interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Success;
            Assert.IsTrue(foundByDescription,
                "Поиск «Markdown» не нашёл Obsidian — локальный поиск не учитывает описание приложения.");

            // Приложение, чьё имя и описание точно не содержат «Markdown» (браузер),
            // не должно остаться в отфильтрованном списке — иначе фильтр не сузил
            // выдачу, а просто перестал работать.
            bool chromeStillVisible = s.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Any(el => (el.Name ?? "") == "Google Chrome");
            Assert.IsFalse(chromeStillVisible,
                "После поиска «Markdown» в списке всё ещё виден Google Chrome — фильтрация по описанию не сужает выдачу.");

            var clearBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnClearSearch"));
            clearBtn?.AsButton().Invoke();
        }

        // ─────────────────────────────────────────────────────────────────────
        // 2. Взаимные переходы «Диагностика» ↔ «Обновления Windows»
        // ─────────────────────────────────────────────────────────────────────

        [TestMethod]
        public void Диагностика_КнопкаПереходаНаWindowsUpdate_СкрытаДоЗапускаПроверки()
        {
            var s = Require();
            NavigateTo(s, "btnDiagnosticsTab");

            // WPF Visibility.Collapsed убирает элемент и из визуального дерева,
            // и из дерева UI Automation — поэтому проверяем именно отсутствие,
            // а не свойство IsOffscreen/значение Visibility.
            var btn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnOpenWindowsUpdate"));
            Assert.IsNull(btn,
                "Кнопка «Открыть Windows Update →» видна на вкладке «Диагностика» ещё до запуска " +
                "проверки — она должна появляться только когда реально найдены ошибки обновления.");
        }

        [TestMethod]
        public void WindowsUpdate_КнопкаПереходаНаДиагностику_СкрытаДоПроверки()
        {
            var s = Require();
            NavigateTo(s, "btnWindowsUpdateTab");

            var btn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnOpenDiagnostics"));
            Assert.IsNull(btn,
                "Кнопка «Почему обновления не ставятся? → Диагностика» видна на вкладке «Обновления " +
                "Windows» до какой-либо проверки — она должна появляться только на проблемных исходах.");
        }

        // ─────────────────────────────────────────────────────────────────────
        // 2а. «Диагностика»: выбор режима Turbo Boost
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Закрывает окно «Успех» после смены режима и возвращает его текст.</summary>
        private static string CloseTurboBoostMessage(AppSession s)
        {
            var box = Retry.WhileNull(
                () => s.MainWindow.ModalWindows.FirstOrDefault(w => w.Title == "Успех" || w.Title == "Ошибка")
                      ?? s.Automation.GetDesktop()
                          .FindAllChildren(cf => cf.ByControlType(ControlType.Window))
                          .FirstOrDefault(w => w.Name == "Успех" || w.Name == "Ошибка")
                          ?.AsWindow(),
                timeout: TimeSpan.FromSeconds(20), interval: TimeSpan.FromMilliseconds(250),
                throwOnTimeout: false).Result;
            if (box == null) return "";

            string text = box.Title + ": " + string.Join(" | ",
                box.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Name));
            box.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button))?.AsButton().Invoke();
            System.Threading.Thread.Sleep(500);
            return text;
        }

        private static string TurboBoostStatus(AppSession s) =>
            s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("txtTurboBoostStatus"))?.Name ?? "";

        private static string ApplyTurboBoostMode(AppSession s, ComboBox combo, string modeName)
        {
            var item = combo.Items.FirstOrDefault(i => (i.Text ?? "") == modeName || (i.Text ?? "").StartsWith(modeName + " —"));
            Assert.IsNotNull(item, $"В списке режимов нет «{modeName}».");
            item!.Select();
            combo.Collapse();
            System.Threading.Thread.Sleep(300);

            var apply = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnApplyTurboBoost"));
            Assert.IsNotNull(apply, "Не найдена кнопка «Применить режим».");
            apply!.AsButton().Invoke();
            return CloseTurboBoostMessage(s);
        }

        [TestMethod]
        public void Диагностика_TurboBoost_СменаРежимаПомечаетЕгоТекущим()
        {
            var s = Require();
            NavigateTo(s, "btnDiagnosticsTab");

            var comboElement = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("cmbTurboBoostMode")),
                timeout: TimeSpan.FromSeconds(15), interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(comboElement, "Не найден список режимов Turbo Boost (cmbTurboBoostMode).");
            var combo = comboElement!.AsComboBox();

            // Режим читается у powercfg при открытии раздела — дожидаемся результата.
            Retry.WhileTrue(() => TurboBoostStatus(s).Contains("определяется"),
                timeout: TimeSpan.FromSeconds(20), interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false);
            string statusBefore = TurboBoostStatus(s);
            const string prefix = "Текущий режим: ";
            if (!statusBefore.StartsWith(prefix) || statusBefore.Contains("не удалось"))
                Assert.Inconclusive($"Режим Turbo Boost на этой машине не читается («{statusBefore}») — менять нечего.");

            // На чистой Windows настройка скрыта: раньше до первого применения здесь было «неизвестно».
            string original = statusBefore.Substring(prefix.Length).Split(" (")[0];
            string currentLabel = combo.SelectedItem?.Text ?? "";
            StringAssert.StartsWith(currentLabel, original, "В списке должен быть выбран текущий режим.");
            StringAssert.Contains(currentLabel, "текущий", "Текущий режим в списке должен быть помечен.");

            // Меняем между двумя соседними режимами: на современных процессорах они равнозначны,
            // так что проверка не отнимает у машины производительность даже на время теста.
            string target = original == "Включён" ? "Агрессивный" : "Включён";
            try
            {
                string message = ApplyTurboBoostMode(s, combo, target);
                StringAssert.Contains(message, "Успех", $"Смена режима не подтверждена окном «Успех»: «{message}».");

                Assert.AreEqual(prefix + target, TurboBoostStatus(s).Split(" (")[0],
                    "Строка состояния не показывает применённый режим.");
                string newLabel = combo.SelectedItem?.Text ?? "";
                StringAssert.StartsWith(newLabel, target, "После применения в списке должен быть выбран новый режим.");
                StringAssert.Contains(newLabel, "текущий", "Пометка «текущий» должна перейти на применённый режим.");
                Assert.IsFalse(combo.Items.Any(i => (i.Text ?? "").StartsWith(original) && (i.Text ?? "").Contains("текущий")),
                    "Пометка «текущий» осталась на прежнем режиме.");
            }
            finally
            {
                // Возвращаем режим, с которым машина пришла в тест.
                ApplyTurboBoostMode(s, combo, original);
            }

            Assert.AreEqual(prefix + original, TurboBoostStatus(s).Split(" (")[0], "Исходный режим не вернулся.");
        }

        // ─────────────────────────────────────────────────────────────────────
        // 3. Блок неуспешных установок в каталоге
        // ─────────────────────────────────────────────────────────────────────

        [TestMethod]
        public void Каталог_БлокНеустановленныхПриложений_СкрытБезНеудачныхУстановок()
        {
            var s = Require();
            NavigateTo(s, "btnCatalogTab");
            System.Threading.Thread.Sleep(500);

            // Свежая сессия без единой попытки установки — HasFailedInstalls
            // должен быть false, и GroupBox с lstFailedInstalls отсутствует
            // в дереве (тот же Collapsed-паттерн, что и у кнопок Диагностика/WU).
            var list = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("lstFailedInstalls"));
            Assert.IsNull(list,
                "Блок «Не установлено» виден в каталоге без единой неудачной установки в этой сессии.");
        }
    }
}
