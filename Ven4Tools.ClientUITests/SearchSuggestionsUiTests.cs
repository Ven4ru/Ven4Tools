using System;
using System.IO;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ven4Tools.ClientUITests
{
    /// <summary>
    /// Проверяет результаты поиска по внешним источникам (когда приложения нет в
    /// каталоге): после удаления Scoop должны остаться только разделы Winget и
    /// Chocolatey, раздела «Scoop» быть не должно.
    ///
    /// Мастер первого запуска обходится записью profile.json (HasSelectedCategory).
    /// </summary>
    [TestClass]
    public class SearchSuggestionsUiTests
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
        private static readonly string ProfilePath = Path.Combine(SettingsDir, "profile.json");

        private static string? _profileBackup;
        private static bool _profileExistedBefore;
        private static AppSession? _session;
        private static string? _launchError;

        private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(10);

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            _profileExistedBefore = File.Exists(ProfilePath);
            if (_profileExistedBefore)
                _profileBackup = File.ReadAllText(ProfilePath);

            Directory.CreateDirectory(SettingsDir);
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
                if (_profileExistedBefore)
                    File.WriteAllText(ProfilePath, _profileBackup!);
                else if (File.Exists(ProfilePath))
                    File.Delete(ProfilePath);
            }
            catch { }
        }

        private static AppSession Require()
        {
            if (_session == null)
                Assert.Inconclusive("Клиент не запущен: " + (_launchError ?? "неизвестная причина"));
            return _session!;
        }

        [TestMethod]
        public void Поиск_НесуществующееВКаталоге_ПоказываетТолькоWingetИChocolatey()
        {
            var s = Require();

            var catalogBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCatalogTab"));
            Assert.IsNotNull(catalogBtn, "Не найдена кнопка вкладки «Каталог».");
            catalogBtn!.AsButton().Invoke();

            var search = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("txtSearch")),
                timeout: ElementTimeout,
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false).Result;
            Assert.IsNotNull(search, "Не найдено поле поиска (txtSearch).");

            // Запрос, которого точно нет в каталоге — тогда клиент опрашивает
            // Winget/Chocolatey напрямую и показывает результаты под строкой поиска.
            // Реальный клик + ввод с клавиатуры (Enter), а не прямая подстановка
            // Text — поле использует placeholder-логику (Tag) и полагается на
            // настоящий TextChanged от живого ввода.
            const string query = "zzz-несуществующий-пакет-ven4tools-test-zzz";
            search!.Click();
            search.AsTextBox().Enter(query);

            // pnlWingetResults/pnlWingetSuggestions — StackPanel/Border, без
            // собственного элемента в дереве UI Automation (как и карточки
            // онбординга): их содержимое ищем прямо в главном окне, без якоря.
            var statusText = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("txtWingetStatus"));

            var settled = Retry.WhileFalse(
                () => (statusText != null && !string.IsNullOrEmpty(statusText.Name)) ||
                      s.MainWindow.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text)
                                                              .And(cf.ByName("📦 Winget"))).Length > 0 ||
                      s.MainWindow.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text)
                                                              .And(cf.ByName("🍫 Chocolatey"))).Length > 0,
                timeout: TimeSpan.FromSeconds(20),
                interval: TimeSpan.FromMilliseconds(500),
                throwOnTimeout: false).Success;
            Assert.IsTrue(settled, "Поиск по внешним источникам не дал никакого результата за 20 секунд " +
                $"(ни разделов, ни статуса «ничего не найдено»). Статус: '{statusText?.Name}'.");

            var scoopMentions = s.MainWindow.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
                .Select(el => el.Name ?? "")
                .Where(t => t.Contains("Scoop"))
                .ToList();

            Assert.IsFalse(scoopMentions.Any(),
                "В результатах поиска всё ещё встречается раздел/упоминание Scoop: " +
                string.Join(" | ", scoopMentions));

            // Не настаиваем, что вингет/чоко обязательно что-то нашли (сеть/пакет
            // могут отсутствовать) — важно только отсутствие Scoop и отсутствие
            // падения UI. Если результатов нет вовсе — это и есть валидный "ничего
            // не найдено" статус из ShowAllSuggestions, тоже без Scoop по определению.
        }
    }
}
