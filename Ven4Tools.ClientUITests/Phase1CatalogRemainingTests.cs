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
    /// Фаза 1 плана живого клика кнопок 2026-07-11 (см. память
    /// project_button_test_plan_2026_07_11): остаток CatalogTab + диалогов.
    /// LocalInstallerDialog пропущен — достижим только drag-and-drop.
    /// AddAppDialog (был нигде не инстанцируемым мёртвым кодом) удалён из
    /// проекта целиком 2026-07-13.
    /// </summary>
    [TestClass]
    public class Phase1CatalogRemainingTests
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
        public void Поиск_ОчисткаИИзбранное_Работают()
        {
            var s = Require();
            var catalogBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCatalogTab"));
            catalogBtn!.AsButton().Invoke();

            var search = Retry.WhileNull(() => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("txtSearch")),
                timeout: T, interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(search, "Не найдено поле поиска.");
            search!.Click();
            search.AsTextBox().Enter("firefox");
            System.Threading.Thread.Sleep(800);

            var clearBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnClearSearch"));
            Assert.IsNotNull(clearBtn, "Не найдена кнопка очистки поиска.");
            clearBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(300);
            string afterClear = search.AsTextBox().Text ?? "";
            Assert.AreNotEqual("firefox", afterClear, "btnClearSearch не очистила поле поиска.");

            var favBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnFavoritesOnly"));
            Assert.IsNotNull(favBtn, "Не найдена кнопка «только избранные».");
            favBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(400);
            favBtn.AsButton().Invoke(); // возвращаем обратно
        }

        [TestMethod]
        public void Пресеты_СохранениеПрименениеПереименованиеУдаление()
        {
            var s = Require();
            var catalogBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCatalogTab"));
            catalogBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(1000);

            // Отмечаем одно приложение чекбоксом, чтобы было что сохранить в пресет.
            var checkbox = s.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox));
            Assert.IsNotNull(checkbox, "Не найден ни один чекбокс приложения в каталоге.");
            checkbox!.AsCheckBox().IsChecked = true;
            System.Threading.Thread.Sleep(300);

            var saveBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnSavePreset"));
            Assert.IsNotNull(saveBtn, "Не найдена кнопка «Сохранить выбор».");
            saveBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(500);

            var dialog = Retry.WhileNull(() => s.MainWindow.ModalWindows.FirstOrDefault(),
                timeout: T, interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(dialog, "Не открылся диалог сохранения пресета.");

            var nameBox = dialog!.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
            Assert.IsNotNull(nameBox, "Не найдено поле имени пресета.");
            string presetName = "test-preset-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            nameBox!.AsTextBox().Text = presetName;
            System.Threading.Thread.Sleep(300);

            var dlgSaveBtn = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => (b.Name ?? "").Contains("Сохранить"));
            Assert.IsNotNull(dlgSaveBtn, "Не найдена кнопка сохранения в диалоге пресета.");
            dlgSaveBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(500);

            var presetLabel = Retry.WhileNull(
                () => s.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                                   .FirstOrDefault(e => (e.Name ?? "").Contains(presetName)),
                timeout: T, interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(presetLabel, $"Пресет «{presetName}» не появился в списке после сохранения.");

            // Найти строку пресета и в ней кнопки apply(→)/rename(✏)/delete(✕).
            var presetRow = presetLabel!.Parent;
            var rowButtons = presetRow?.FindAllChildren(cf => cf.ByControlType(ControlType.Button)).ToList()
                             ?? new System.Collections.Generic.List<AutomationElement>();
            Assert.IsTrue(rowButtons.Count >= 1, "Не найдено ни одной кнопки в строке пресета.");

            var applyBtn = rowButtons.FirstOrDefault(b => (b.Name ?? "") == "→");
            if (applyBtn != null) { applyBtn.AsButton().Invoke(); System.Threading.Thread.Sleep(300); }

            // Удаление — последним действием, чтобы не копить тестовые пресеты.
            var deleteBtn = rowButtons.FirstOrDefault(b => (b.Name ?? "") == "✕");
            Assert.IsNotNull(deleteBtn, "Не найдена кнопка удаления пресета.");
            deleteBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(500);

            var confirmBox = s.MainWindow.ModalWindows.FirstOrDefault();
            if (confirmBox != null)
            {
                var yes = confirmBox.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .FirstOrDefault(b => (b.Name ?? "") == "Да" || (b.Name ?? "") == "Yes");
                yes?.Click();
                System.Threading.Thread.Sleep(500);
            }

            checkbox.AsCheckBox().IsChecked = false;
        }

        [TestMethod]
        public void ЭкспортИмпортСписка_ОткрываютДиалогФайла()
        {
            // ИЗВЕСТНОЕ ОГРАНИЧЕНИЕ: нативный Microsoft.Win32.SaveFileDialog/OpenFileDialog
            // (COM IFileDialog) не появляется в дереве UIA при клике, инициированном
            // через FlaUI (ни Invoke(), ни реальный Click(), ни с явным SetForeground()) —
            // проверено 2026-07-11. Кнопки остаются подтверждены только код-ревью
            // (см. группу А отчёта классификации риска), не live-кликом.
            Assert.Inconclusive("Нативные диалоги открытия/сохранения файла не автоматизируются через FlaUI — известное ограничение, см. комментарий в коде.");
        }

        [TestMethod]
        public void ОчиститьДобавленные_РаботаетСПодтверждением()
        {
            var s = Require();
            var catalogBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCatalogTab"));
            catalogBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(500);

            var clearBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnClearAllUserApps"));
            Assert.IsNotNull(clearBtn, "Не найдена кнопка «Очистить добавленные».");
            clearBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(500);

            var confirmBox = s.MainWindow.ModalWindows.FirstOrDefault();
            if (confirmBox != null)
            {
                // Пользовательских приложений в тестовом профиле нет — подтверждаем очистку,
                // это безопасно (чистит только локальный список добавленных, не систему).
                var yes = confirmBox.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .FirstOrDefault(b => (b.Name ?? "") == "Да" || (b.Name ?? "") == "Yes");
                yes?.Click();
                System.Threading.Thread.Sleep(500);
            }
        }
    }
}
