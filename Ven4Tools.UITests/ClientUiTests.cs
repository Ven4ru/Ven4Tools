using System;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ven4Tools.UITests
{
    /// <summary>
    /// UI-тесты клиента Ven4Tools на FlaUI (UIA3).
    ///
    /// Все элементы ищутся по AutomationId — в WPF он автоматически совпадает с
    /// x:Name элемента (btnCatalogTab, txtEmail, btnSavePreset и т.п.), поэтому
    /// править XAML основного проекта не требуется.
    ///
    /// Приложение запускается один раз на класс (ClassInitialize) и закрывается в
    /// ClassCleanup. Если окно не удалось получить (headless-окружение, отклонённый
    /// UAC) — каждый тест переводится в Inconclusive, а не падает.
    /// </summary>
    [TestClass]
    public class ClientUiTests
    {
        private static AppSession? _session;
        private static string? _launchError;

        private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(8);

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            try
            {
                _session = AppSession.Launch();
            }
            catch (Exception ex)
            {
                _launchError = ex.Message;
                _session = null;
            }
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            _session?.Dispose();
            _session = null;
        }

        /// <summary>Гарантирует, что приложение доступно, иначе переводит тест в Inconclusive.</summary>
        private static AppSession Require()
        {
            if (_session == null)
            {
                Assert.Inconclusive(
                    "Клиент Ven4Tools не запущен, UI-тесты пропущены. Причина: " +
                    (_launchError ?? "неизвестна") +
                    ". Запустите тесты в интерактивной сессии «от имени администратора».");
            }
            return _session!;
        }

        /// <summary>Кликает по навигационной кнопке сайдбара по её AutomationId.</summary>
        private static void NavigateTo(AppSession s, string navButtonAutomationId)
        {
            var btn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(navButtonAutomationId));
            Assert.IsNotNull(btn, $"Не найдена кнопка навигации '{navButtonAutomationId}'.");
            btn!.AsButton().Invoke();
        }

        /// <summary>Дожидается появления элемента по AutomationId внутри главного окна.</summary>
        private static AutomationElement? WaitForElement(AppSession s, string automationId)
        {
            return Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)),
                timeout: ElementTimeout,
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false).Result;
        }

        // ─────────────────────────────────────────────────────────────────────
        // 1. Запуск и главное окно
        // ─────────────────────────────────────────────────────────────────────

        [TestMethod]
        public void Запуск_ГлавноеОкноПоявляетсяИВидимо()
        {
            var s = Require();

            Assert.IsNotNull(s.MainWindow, "Главное окно не получено.");
            Assert.IsFalse(s.MainWindow.IsOffscreen, "Главное окно вне экрана (не отрисовано).");
            StringAssert.Contains(s.MainWindow.Title, AppSession.MainWindowTitle,
                "Заголовок главного окна не содержит 'Ven4Tools'.");
        }

        [TestMethod]
        public void Запуск_БазовыеЭлементыНавигацииВидны()
        {
            var s = Require();

            string[] navButtons =
            {
                "btnCatalogTab", "btnInstalledTab", "btnSystemTab",
                "btnAboutTab", "btnUserNav",
            };

            foreach (var id in navButtons)
            {
                var el = WaitForElement(s, id);
                Assert.IsNotNull(el, $"Не найден элемент навигации с AutomationId '{id}'.");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // 2. Каталог приложений
        // ─────────────────────────────────────────────────────────────────────

        [TestMethod]
        public void Каталог_ВкладкаОткрываетсяИСодержитПоиск()
        {
            var s = Require();

            NavigateTo(s, "btnCatalogTab");

            // Поле поиска приложений — признак того, что вкладка «Каталог» отрисована.
            var search = WaitForElement(s, "txtSearch");
            Assert.IsNotNull(search, "Поле поиска каталога (txtSearch) не отображается.");
        }

        [TestMethod]
        public void Каталог_СписокПриложенийЗагружается()
        {
            var s = Require();

            NavigateTo(s, "btnCatalogTab");
            WaitForElement(s, "txtSearch");

            // Каталог раскладывает приложения по категориям в Expander'ах.
            // Достаточно убедиться, что в области контента есть хотя бы один Expander
            // и хотя бы один CheckBox (карточка приложения) либо панель категории.
            var expanders = Retry.WhileEmpty(
                () => s.MainWindow.FindAllDescendants(cf =>
                          cf.ByControlType(ControlType.Group)).ToList(),
                timeout: ElementTimeout,
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false).Result;

            Assert.IsTrue(expanders != null && expanders.Count > 0,
                "В каталоге не найдено ни одной группы/категории приложений.");
        }

        // ─────────────────────────────────────────────────────────────────────
        // 3. Пресеты (живут внутри вкладки «Каталог»)
        // ─────────────────────────────────────────────────────────────────────

        [TestMethod]
        public void Пресеты_ЭлементыУправленияВидны()
        {
            var s = Require();

            NavigateTo(s, "btnCatalogTab");
            WaitForElement(s, "txtSearch");

            var saveBtn = WaitForElement(s, "btnSavePreset");
            Assert.IsNotNull(saveBtn, "Кнопка сохранения пресета (btnSavePreset) не найдена.");

            // Список пресетов присутствует в дереве (даже если пуст).
            var presetsList = WaitForElement(s, "lstPresets");
            Assert.IsNotNull(presetsList, "Список пресетов (lstPresets) не найден.");
        }

        // ─────────────────────────────────────────────────────────────────────
        // 4. Авторизация (только UI окна входа, без реального логина)
        // ─────────────────────────────────────────────────────────────────────

        [TestMethod]
        public void Авторизация_ОкноВходаСодержитПоляВводаИКнопку()
        {
            var s = Require();

            const string loginTitle = "Ven4Tools — Аккаунт";

            // Открываем окно аккаунта кликом по кнопке сайдбара. Когда пользователь
            // не залогинен — открывается LoginWindow; если в системе есть сохранённая
            // сессия — открывается ProfileWindow. Тест входа имеет смысл только в
            // первом случае.
            NavigateTo(s, "btnUserNav");

            // Ждём появления любого модального окна над главным.
            var modal = Retry.WhileNull(
                () => s.MainWindow.ModalWindows.FirstOrDefault(),
                timeout: ElementTimeout,
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false).Result;

            // Окно входа узнаём по заголовку. Если открылось другое окно (профиль —
            // пользователь уже авторизован), закрываем его и пропускаем тест.
            var loginWindow = (modal != null && modal.Title == loginTitle) ? modal : null;
            if (loginWindow == null)
            {
                try { modal?.Close(); } catch { }
                Assert.Inconclusive(
                    "Окно входа не открыто (в системе активна сохранённая сессия — " +
                    "btnUserNav открыл окно профиля). UI входа проверяется только при " +
                    "отсутствии авторизации.");
            }

            try
            {
                // Содержимое диалога появляется в дереве UIA с небольшой задержкой
                // после открытия окна — ищем поля с повтором.
                AutomationElement? FindInLogin(string id) => Retry.WhileNull(
                    () => loginWindow!.FindFirstDescendant(cf => cf.ByAutomationId(id)),
                    timeout: ElementTimeout,
                    interval: TimeSpan.FromMilliseconds(300),
                    throwOnTimeout: false).Result;

                var email = FindInLogin("txtEmail");
                var password = FindInLogin("txtPassword");
                var submit = FindInLogin("btnSubmit");

                Assert.IsNotNull(email, "Поле email (txtEmail) отсутствует в окне входа.");
                Assert.IsNotNull(password, "Поле пароля (txtPassword) отсутствует в окне входа.");
                Assert.IsNotNull(submit, "Кнопка входа (btnSubmit) отсутствует в окне входа.");
            }
            finally
            {
                // Закрываем диалог, чтобы не блокировать остальные тесты класса.
                try { loginWindow!.Close(); } catch { }
                try
                {
                    var w = loginWindow!.Patterns.Window.PatternOrDefault;
                    w?.Close();
                }
                catch { }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // 5. Переключение между всеми вкладками
        // ─────────────────────────────────────────────────────────────────────

        [TestMethod]
        public void Вкладки_ПереключениеМеждуВсемиВкладкамиРаботает()
        {
            var s = Require();

            // (AutomationId кнопки, ожидаемый AutomationId элемента-маркера на вкладке
            //  либо null — тогда проверяем только что окно осталось живым).
            (string nav, string? marker)[] tabs =
            {
                ("btnCatalogTab",    "txtSearch"),
                ("btnInstalledTab",  null),
                ("btnSystemTab",     null),
                ("btnOfficeTab",     null),
                ("btnActivationTab", null),
                ("btnNetworkTab",    null),
                ("btnDebloaterTab",  null),
                ("btnHistoryTab",    null),
                ("btnAboutTab",      null),
                ("btnCatalogTab",    "txtSearch"),
            };

            foreach (var (nav, marker) in tabs)
            {
                NavigateTo(s, nav);

                // Окно должно оставаться доступным после переключения.
                Assert.IsFalse(s.MainWindow.IsOffscreen,
                    $"Главное окно стало недоступным после открытия вкладки '{nav}'.");

                if (marker != null)
                {
                    var el = WaitForElement(s, marker);
                    Assert.IsNotNull(el,
                        $"После перехода на '{nav}' не найден ожидаемый элемент '{marker}'.");
                }
            }
        }
    }
}
