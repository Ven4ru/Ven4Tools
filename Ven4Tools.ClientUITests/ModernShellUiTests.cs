using System;
using System.IO;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ven4Tools.ClientUITests
{
    /// <summary>
    /// Новая оболочка главного окна и возврат к прежней.
    ///
    /// Остальные классы набора запускают клиент в прежней оболочке (см.
    /// <see cref="AppSession.Launch(bool)"/>) и проверяют разделы. Здесь проверяется
    /// только то, чем оболочки различаются: стартовый «Обзор», свёрнутые группы меню,
    /// кнопка переключения и то, что выбор записывается в профиль.
    /// </summary>
    [TestClass]
    public class ModernShellUiTests
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
        private static readonly string ProfilePath = Path.Combine(SettingsDir, "profile.json");

        private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(8);

        private static string? _profileBackup;
        private static bool _profileExisted;
        private static AppSession? _session;
        private static string? _launchError;

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            Directory.CreateDirectory(SettingsDir);
            _profileExisted = File.Exists(ProfilePath);
            if (_profileExisted) _profileBackup = File.ReadAllText(ProfilePath);
            File.WriteAllText(ProfilePath,
                "{\"CatalogMode\":\"full\",\"HasSelectedCategory\":true,\"UiMode\":\"modern\"}");

            try { _session = AppSession.Launch(modernUi: true); }
            catch (Exception ex) { _launchError = ex.Message; _session = null; }
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            _session?.Dispose();
            _session = null;
            try
            {
                if (_profileExisted && _profileBackup != null) File.WriteAllText(ProfilePath, _profileBackup);
                else if (File.Exists(ProfilePath)) File.Delete(ProfilePath);
            }
            catch { /* восстановление профиля — по возможности */ }
        }

        private static AppSession Require()
        {
            if (_session == null)
            {
                Assert.Inconclusive("Клиент Ven4Tools не запущен, UI-тесты пропущены. Причина: " +
                                    (_launchError ?? "неизвестна"));
            }
            return _session!;
        }

        private static AutomationElement? Find(AppSession s, string automationId) =>
            s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

        private static AutomationElement? WaitFor(AppSession s, string automationId) =>
            Retry.WhileNull(() => Find(s, automationId), timeout: ElementTimeout,
                interval: TimeSpan.FromMilliseconds(250), throwOnTimeout: false).Result;

        private static bool WaitGone(AppSession s, string automationId) =>
            Retry.WhileTrue(() => Find(s, automationId) is { IsOffscreen: false }, timeout: ElementTimeout,
                interval: TimeSpan.FromMilliseconds(250), throwOnTimeout: false).Success;

        private static string ProfileText() =>
            Retry.WhileException(() => File.ReadAllText(ProfilePath), timeout: TimeSpan.FromSeconds(3),
                interval: TimeSpan.FromMilliseconds(200)).Result ?? "";

        [TestMethod]
        public void НоваяОболочка_Обзор_ГруппыМеню_ВозвратКПрежнейИОбратно()
        {
            var s = Require();

            // ── Старт: «Обзор», меню свёрнуто до шести пунктов ──
            Assert.IsNotNull(WaitFor(s, "btnOverviewTab"), "В новой оболочке нет пункта «Обзор».");
            Assert.IsNotNull(WaitFor(s, "txtOverviewSummary"), "Клиент в новой оболочке должен открываться на «Обзоре».");
            Assert.IsNotNull(Find(s, "btnCatalogTab"), "Пункт «Каталог» должен быть в меню новой оболочки.");
            Assert.IsNull(Find(s, "btnDebloaterTab"),
                "«Очистка» должна быть спрятана в группу «Windows», пока группа свёрнута.");
            Assert.IsNull(Find(s, "btnBenchmarkTab"),
                "«Бенчмарк» должен быть спрятан в группу «Сервис», пока группа свёрнута.");

            var switchButton = Find(s, "btnUiModeSwitch");
            Assert.IsNotNull(switchButton, "Нет кнопки переключения оболочки.");
            Assert.AreEqual("Старый интерфейс", switchButton!.Name);

            // ── Группа раскрывается и ведёт в раздел ──
            Find(s, "btnGroupWindows")!.AsButton().Invoke();
            var debloater = WaitFor(s, "btnDebloaterTab");
            Assert.IsNotNull(debloater, "Группа «Windows» не раскрылась.");
            debloater!.AsButton().Invoke();
            Assert.IsTrue(
                Retry.WhileFalse(() => Find(s, "txtHeaderTitle")?.Name == "Очистка", timeout: ElementTimeout,
                    interval: TimeSpan.FromMilliseconds(250), throwOnTimeout: false).Success,
                "В шапке новой оболочки должно стоять название открытого раздела.");

            // ── Быстрое действие «Обзора» ведёт в каталог ──
            Find(s, "btnOverviewTab")!.AsButton().Invoke();
            var toCatalog = WaitFor(s, "btnOverviewCatalog");
            Assert.IsNotNull(toCatalog, "На «Обзоре» нет действия «Собрать набор программ».");
            toCatalog!.AsButton().Invoke();
            Assert.IsNotNull(WaitFor(s, "btnInstall"), "Действие «Обзора» должно открывать каталог.");

            // ── Возврат к прежней оболочке ──
            Find(s, "btnUiModeSwitch")!.AsButton().Invoke();
            Assert.IsNotNull(WaitFor(s, "btnDebloaterTab"), "В прежней оболочке все разделы — отдельными пунктами.");
            Assert.IsNotNull(Find(s, "btnBenchmarkTab"), "В прежней оболочке «Бенчмарк» — отдельный пункт.");
            Assert.IsTrue(WaitGone(s, "btnOverviewTab"), "В прежней оболочке пункта «Обзор» быть не должно.");
            Assert.AreEqual("Новый интерфейс", Find(s, "btnUiModeSwitch")!.Name);
            StringAssert.Contains(ProfileText(), "classic", "Выбор прежней оболочки должен записаться в профиль.");

            // ── И обратно ──
            Find(s, "btnUiModeSwitch")!.AsButton().Invoke();
            Assert.IsNotNull(WaitFor(s, "btnOverviewTab"), "Новая оболочка не вернулась.");
            Assert.IsNotNull(WaitFor(s, "txtOverviewSummary"), "После возврата в новую оболочку должен открыться «Обзор».");
            StringAssert.Contains(ProfileText(), "modern", "Выбор новой оболочки должен записаться в профиль.");
        }
    }
}
