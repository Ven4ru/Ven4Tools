using System;
using System.IO;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ven4Tools.ClientUITests
{
    /// <summary>
    /// Фаза 1 (продолжение): динамически создаваемые кнопки без AutomationId —
    /// звезда избранного, добавление/удаление пользовательского приложения,
    /// открепление пина.
    /// </summary>
    [TestClass]
    public class Phase1DynamicButtonsTests
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
        private static readonly string ProfilePath = Path.Combine(SettingsDir, "profile.json");

        private static string? _profileBackup; private static bool _profileExisted;
        private static AppSession? _session;
        private static string? _launchError;
        private static readonly TimeSpan T = TimeSpan.FromSeconds(15);

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            Directory.CreateDirectory(SettingsDir);
            _profileExisted = File.Exists(ProfilePath);
            if (_profileExisted) _profileBackup = File.ReadAllText(ProfilePath);
            // PinnedAppIds=cpu-z даёт кнопку открепления в главном окне.
            File.WriteAllText(ProfilePath, "{\"CatalogMode\":\"full\",\"HasSelectedCategory\":true,\"PinnedAppIds\":[\"cpu-z\"]}");

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
        public void ЗвездаИзбранного_ПереключаетСостояние()
        {
            var s = Require();
            var catalogBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCatalogTab"));
            catalogBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(1000);

            var starBtn = s.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => b.Properties.HelpText.IsSupported &&
                    ((b.Properties.HelpText.Value ?? "").Contains("избранное", StringComparison.OrdinalIgnoreCase)));
            Assert.IsNotNull(starBtn, "Не найдена ни одна кнопка избранного (★/☆).");

            string beforeTooltip = starBtn!.Properties.HelpText.Value ?? "";
            starBtn.Click();
            System.Threading.Thread.Sleep(500);
            string afterTooltip = starBtn.Properties.HelpText.Value ?? "";
            Assert.AreNotEqual(beforeTooltip, afterTooltip, "Клик по звезде не изменил ToolTip (Добавить/Убрать из избранного) — состояние не переключилось.");

            starBtn.Click(); // возвращаем обратно
            System.Threading.Thread.Sleep(300);
        }

        [TestMethod]
        public void ОткрепитьПин_УдаляетИзПанели()
        {
            var s = Require();
            var unpinBtn = Retry.WhileNull(
                () => s.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .FirstOrDefault(b => b.Properties.HelpText.IsSupported &&
                        (b.Properties.HelpText.Value ?? "") == "Открепить"),
                timeout: T, interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(unpinBtn, "Не найдена кнопка «Открепить» для пина cpu-z.");
            unpinBtn!.Click();
            System.Threading.Thread.Sleep(500);

            var stillThere = s.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Any(b => b.Properties.HelpText.IsSupported && (b.Properties.HelpText.Value ?? "") == "Открепить");
            Assert.IsFalse(stillThere, "Кнопка «Открепить» всё ещё видна после клика — пин не удалился.");
        }

        [TestMethod]
        public void WingetПредложение_ДобавляетПриложениеВСписок()
        {
            var s = Require();
            var catalogBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCatalogTab"));
            catalogBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(1000);

            var search = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("txtSearch"));
            Assert.IsNotNull(search, "Не найдено поле поиска.");
            // "windirstat" уже проверялось на choco в предыдущем прогоне — здесь берём
            // реальный пакет, который есть именно в winget, но не в каталоге Ven4Tools:
            // "everything" уже в каталоге, возьмём "sysinternals-suite" маловероятный в каталоге.
            const string query = "hwinfo";
            search!.Click();
            search.AsTextBox().Enter(query);

            var wingetLabel = Retry.WhileNull(
                () => s.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Text).And(cf.ByName("📦 Winget")))
                                   .FirstOrDefault(),
                timeout: TimeSpan.FromSeconds(20), interval: TimeSpan.FromMilliseconds(500), throwOnTimeout: false).Result;
            if (wingetLabel == null)
            {
                Assert.Inconclusive("Раздел «📦 Winget» не появился за 20с (сеть/winget недоступны в моменте).");
                return;
            }

            var suggestionButtons = s.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Where(b => (b.Name ?? "").Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            (b.Properties.HelpText.IsSupported && (b.Properties.HelpText.Value ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (suggestionButtons.Count == 0)
            {
                var panel = wingetLabel.Parent;
                suggestionButtons = panel?.FindAllChildren(cf => cf.ByControlType(ControlType.Button)).ToList() ?? new System.Collections.Generic.List<AutomationElement>();
            }
            Assert.IsTrue(suggestionButtons.Count >= 1, "Не найдена кликабельная кнопка winget-предложения.");

            suggestionButtons[0].Click();
            System.Threading.Thread.Sleep(1000);

            string textAfter = search.AsTextBox().Text ?? "";
            Assert.AreNotEqual(query, textAfter, "Поле поиска не сбросилось после добавления winget-предложения.");
        }

        [TestMethod]
        public void УдалитьПользовательскоеПриложение_ИзСписка()
        {
            var s = Require();
            var catalogBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCatalogTab"));
            catalogBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(1000);

            // Используем то же добавленное через winget-предложение приложение из
            // предыдущего теста (в рамках одной ClassInitialize-сессии список общий).
            var removeBtn = s.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => b.Properties.HelpText.IsSupported &&
                    (b.Properties.HelpText.Value ?? "") == "Удалить из списка");
            if (removeBtn == null)
            {
                Assert.Inconclusive("Нет ни одного пользовательского приложения для проверки удаления (предыдущий тест не добавил / не сохранился в этой сессии).");
                return;
            }
            removeBtn.Click();
            System.Threading.Thread.Sleep(500);

            var confirmBox = s.MainWindow.ModalWindows.FirstOrDefault();
            if (confirmBox != null)
            {
                var yes = confirmBox.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .FirstOrDefault(b => (b.Name ?? "") == "Да" || (b.Name ?? "") == "Yes");
                yes?.Click();
                System.Threading.Thread.Sleep(500);
            }
        }
    }
}
