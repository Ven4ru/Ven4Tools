using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ven4Tools.ClientUITests
{
    /// <summary>
    /// Проверяет экран настроек приоритета источников установки (вкладка «Система»):
    /// после удаления Scoop должно остаться ровно 3 источника, и переупорядочивание
    /// через UI должно реально сохраняться на диск в source_order.json.
    ///
    /// Мастер первого запуска обходится напрямую записью profile.json
    /// (HasSelectedCategory=true) — сценарий не про онбординг, а про настройки.
    /// </summary>
    [TestClass]
    public class SourceOrderSettingsUiTests
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");

        private static readonly string SourceOrderPath = Path.Combine(SettingsDir, "source_order.json");
        private static readonly string ProfilePath = Path.Combine(SettingsDir, "profile.json");

        private static string? _sourceOrderBackup;
        private static bool _sourceOrderExistedBefore;
        private static string? _profileBackup;
        private static bool _profileExistedBefore;

        private static AppSession? _session;
        private static string? _launchError;

        private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(10);

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            _sourceOrderExistedBefore = File.Exists(SourceOrderPath);
            if (_sourceOrderExistedBefore)
                _sourceOrderBackup = File.ReadAllText(SourceOrderPath);

            _profileExistedBefore = File.Exists(ProfilePath);
            if (_profileExistedBefore)
                _profileBackup = File.ReadAllText(ProfilePath);

            Directory.CreateDirectory(SettingsDir);
            if (File.Exists(SourceOrderPath)) File.Delete(SourceOrderPath); // проверяем дефолт "с нуля"
            File.WriteAllText(ProfilePath,
                "{\"CatalogMode\":\"full\",\"HasSelectedCategory\":true}");

            try { _session = AppSession.Launch(); }
            catch (Exception ex) { _launchError = ex.Message; _session = null; }
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            _session?.Dispose();
            _session = null;

            // Возможна короткая гонка с диском сразу после Kill() процесса —
            // не даём сбою очистки маскировать реальный результат теста.
            try
            {
                if (_sourceOrderExistedBefore)
                    File.WriteAllText(SourceOrderPath, _sourceOrderBackup!);
                else if (File.Exists(SourceOrderPath))
                    File.Delete(SourceOrderPath);
            }
            catch { }

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
        public void ПорядокИсточников_РовноТриБезScoop_ИСохраняетсяПослеИзменения()
        {
            var s = Require();

            var systemBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnSystemTab"));
            Assert.IsNotNull(systemBtn, "Не найдена кнопка вкладки «Система».");
            systemBtn!.AsButton().Invoke();

            var sourceList = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("lstSourceOrder")),
                timeout: ElementTimeout,
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false).Result;
            Assert.IsNotNull(sourceList, "Не найден список порядка источников (lstSourceOrder).");

            // ListBoxItem.Name по умолчанию — это ToString() привязанного SourceItem,
            // а не текст Label; реальный текст — в дочернем TextBlock (Binding Label).
            static string ItemLabel(AutomationElement item) =>
                item.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))?.Name
                ?? item.Name ?? "";

            var items = sourceList!.FindAllChildren();
            Assert.AreEqual(3, items.Length,
                $"Ожидалось ровно 3 источника (Winget, Direct, Choco), найдено {items.Length}: " +
                string.Join(" | ", items.Select(ItemLabel)));

            var labels = items.Select(ItemLabel).ToList();
            Assert.IsFalse(labels.Any(l => l.Contains("Scoop")),
                "В списке источников всё ещё встречается Scoop: " + string.Join(" | ", labels));

            string firstLabelBefore = labels[0];

            // Выбираем первый пункт и двигаем его вниз кнопкой ▼ — простое, надёжное
            // переупорядочивание без drag&drop, ровно то, что предлагает сам UI.
            // Клик и выбор в WPF ListBox иногда не успевают дойти до обработчика
            // кнопки за один проход (гонка событий) — повторяем несколько раз.
            var btnDown = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnSrcDown"));
            Assert.IsNotNull(btnDown, "Не найдена кнопка ▼ (btnSrcDown).");

            List<string> itemsAfterMove = labels;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                sourceList.FindAllChildren()[0].Click();
                System.Threading.Thread.Sleep(400);
                btnDown!.AsButton().Invoke();
                System.Threading.Thread.Sleep(300);

                itemsAfterMove = sourceList.FindAllChildren().Select(ItemLabel).ToList();
                if (itemsAfterMove[0] != firstLabelBefore) break;
            }

            Assert.AreNotEqual(firstLabelBefore, itemsAfterMove[0],
                "Порядок в списке не изменился после нажатия ▼ за 5 попыток — возможно, кнопка не сработала.");

            var saveBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnSaveSourceOrder"));
            Assert.IsNotNull(saveBtn, "Не найдена кнопка сохранения порядка источников.");
            saveBtn!.AsButton().Invoke();

            var statusText = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("txtSourceOrderStatus")),
                timeout: ElementTimeout,
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false).Result;
            Assert.IsNotNull(statusText, "Не найден текст статуса сохранения (txtSourceOrderStatus).");

            Retry.WhileEmpty(
                () => statusText!.Name ?? "",
                timeout: TimeSpan.FromSeconds(10),
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false);

            // Настоящая проверка — реальный файл на диске, а не только текст в UI.
            Assert.IsTrue(File.Exists(SourceOrderPath),
                "После сохранения source_order.json не появился на диске.");
            string savedJson = File.ReadAllText(SourceOrderPath);
            Assert.IsFalse(savedJson.Contains("scoop", StringComparison.OrdinalIgnoreCase),
                "Сохранённый source_order.json всё ещё содержит scoop: " + savedJson);
            Assert.IsTrue(savedJson.Contains("winget") && savedJson.Contains("direct") && savedJson.Contains("choco"),
                "Сохранённый source_order.json не содержит всех трёх ожидаемых источников: " + savedJson);
        }
    }
}
