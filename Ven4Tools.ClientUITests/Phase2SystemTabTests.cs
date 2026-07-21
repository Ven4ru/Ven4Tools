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
    /// Фаза 2 плана 2026-07-11: остаток SystemTab (все 4 под-вкладки, кроме
    /// «Общие» — покрыта раньше). Кнопки, открывающие нативные Save/OpenFileDialog
    /// (btnBrowseCachePath, btnExportSettings, btnImportSettings) — то же
    /// известное ограничение FlaUI, что и в Фазе 1, не дублируем расследование.
    /// </summary>
    [TestClass]
    public class Phase2SystemTabTests
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
        private static readonly string ProfilePath = Path.Combine(SettingsDir, "profile.json");
        private static readonly string SourceOrderPath = Path.Combine(SettingsDir, "source_order.json");

        private static string? _profileBackup; private static bool _profileExisted;
        private static string? _sourceOrderBackup; private static bool _sourceOrderExisted;
        private static AppSession? _session;
        private static string? _launchError;
        private static readonly TimeSpan T = TimeSpan.FromSeconds(15);

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            // Реальный source_order.json мог остаться в режиме "per_category" от
            // предыдущих тестов сессии — тогда pnlGlobalOrder (и btnSrcUp внутри)
            // скрыт по делу, не баг. Форсируем "global" на время этого класса.
            _sourceOrderExisted = File.Exists(SourceOrderPath);
            if (_sourceOrderExisted) _sourceOrderBackup = File.ReadAllText(SourceOrderPath);
            File.WriteAllText(SourceOrderPath, "{\"Mode\":\"global\",\"GlobalOrder\":[\"winget\",\"direct\",\"choco\"],\"CategoryPrimary\":{}}");
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
            try
            {
                if (_sourceOrderExisted) File.WriteAllText(SourceOrderPath, _sourceOrderBackup!);
                else if (File.Exists(SourceOrderPath)) File.Delete(SourceOrderPath);
            }
            catch { }
        }

        private static AppSession Require()
        {
            if (_session == null) Assert.Inconclusive("Клиент не запущен: " + (_launchError ?? "неизвестная причина"));
            return _session!;
        }

        private static void GoToSystemSubTab(AppSession s, string subTabName)
        {
            var systemBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnSystemTab"));
            Assert.IsNotNull(systemBtn, "Не найдена кнопка вкладки «Система».");
            systemBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(500);

            var subTab = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.TabItem).And(cf.ByName(subTabName))),
                timeout: T, interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(subTab, $"Не найдена под-вкладка «{subTabName}».");
            subTab!.Click();
            System.Threading.Thread.Sleep(400);
        }

        [TestMethod]
        public void Источники_КнопкаВверх_Существует()
        {
            var s = Require();
            GoToSystemSubTab(s, "Источники");
            var upBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnSrcUp"));
            Assert.IsNotNull(upBtn, "Не найдена кнопка ▲.");
            // Верхний элемент списка — движение вверх не изменит порядок (уже наверху),
            // но клик не должен падать с исключением.
            upBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(300);
        }

        [TestMethod]
        public void ОфлайнИПриватность_ВыборКэшаИЗагрузка()
        {
            var s = Require();
            GoToSystemSubTab(s, "Офлайн и приватность");

            var selectAllBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCacheSelectAll"));
            Assert.IsNotNull(selectAllBtn, "Не найдена кнопка «Все» (выбор кэша).");
            selectAllBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(300);

            var selectNoneBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCacheSelectNone"));
            Assert.IsNotNull(selectNoneBtn, "Не найдена кнопка «Сброс» (выбор кэша).");
            selectNoneBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(300);

            var openCacheBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnOpenCacheFolder"));
            Assert.IsNotNull(openCacheBtn, "Не найдена кнопка «Открыть» (папка кэша).");
            openCacheBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(1000); // откроет окно проводника

            // Очистка кэша — только подтверждение-отказ, как и для логов.
            var clearCacheBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnClearCache"));
            Assert.IsNotNull(clearCacheBtn, "Не найдена кнопка «Очистить» (кэш).");
            clearCacheBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(500);
            var confirmBox = s.MainWindow.ModalWindows.FirstOrDefault();
            if (confirmBox != null)
            {
                var no = confirmBox.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .FirstOrDefault(b => (b.Name ?? "") == "Нет" || (b.Name ?? "") == "No");
                no?.Click();
                System.Threading.Thread.Sleep(300);
            }
        }

        [TestMethod]
        public void ПрофильИСнимки_СохранитьИУдалитьСнапшот()
        {
            var s = Require();
            GoToSystemSubTab(s, "Профиль и снимки");

            var saveSnapBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnSaveSnapshot"));
            Assert.IsNotNull(saveSnapBtn, "Не найдена кнопка «Сохранить снапшот».");
            saveSnapBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(500);

            var dialog = Retry.WhileNull(() => s.MainWindow.ModalWindows.FirstOrDefault(),
                timeout: T, interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(dialog, "Не открылся диалог имени снапшота.");
            var nameBox = dialog!.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
            Assert.IsNotNull(nameBox, "Не найдено поле имени снапшота.");
            string snapName = "test-snap-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            nameBox!.AsTextBox().Text = snapName;
            System.Threading.Thread.Sleep(300);
            var dlgSave = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => (b.Name ?? "").Contains("Сохранить"));
            Assert.IsNotNull(dlgSave, "Не найдена кнопка сохранения в диалоге снапшота.");
            dlgSave!.AsButton().Invoke();
            System.Threading.Thread.Sleep(500);

            var snapLabel = Retry.WhileNull(
                () => s.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                                   .FirstOrDefault(e => (e.Name ?? "").Contains(snapName)),
                timeout: T, interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(snapLabel, $"Снапшот «{snapName}» не появился в списке.");

            var row = snapLabel!.Parent;
            var deleteBtn = row?.FindAllChildren(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => (b.Name ?? "") == "✕");
            Assert.IsNotNull(deleteBtn, "Не найдена кнопка удаления снапшота.");
            deleteBtn!.Click();
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

        [TestMethod]
        public void НативныеДиалогиФайлов_ИзвестноеОграничение()
        {
            // btnBrowseCachePath / btnExportSettings / btnImportSettings — то же
            // ограничение FlaUI с COM IFileDialog, что и в Фазе 1 (CatalogTab
            // экспорт/импорт списка). Не тратим время на повторное расследование.
            Assert.Inconclusive("Нативные Save/OpenFileDialog не автоматизируются через FlaUI — известное ограничение (см. Фазу 1).");
        }
    }
}
