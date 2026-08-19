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
    /// Рантайм-проверка кнопки «📥 Набор с сайта» и диалога кода набора.
    ///
    /// Фича безопасна для реального клика: она ничего не устанавливает, не
    /// трогает систему и (после отказа от серверного хранения) не делает ни
    /// одного сетевого запроса — только отмечает чекбоксы в каталоге.
    /// Поэтому здесь всё кликается вживую, без «код-ревью вместо клика».
    ///
    /// Мастер первого запуска обходится записью profile.json, каталог
    /// переводится в режим full — иначе части приложений из кода не будет
    /// в списке и проверка «отмечено ровно то, что просили» станет ложной.
    /// </summary>
    [TestClass]
    public class PresetCodeUiTests
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");

        private static readonly string ProfilePath = Path.Combine(SettingsDir, "profile.json");

        // Неотправленный отчёт о сбое клиент показывает отдельным окном при
        // старте — оно перехватывает запуск, и тесты виснут на поиске главного
        // окна. Убираем его на время прогона и возвращаем после.
        private static readonly string CrashPath = Path.Combine(SettingsDir, "crash_last.json");

        private static string? _profileBackup;
        private static bool _profileExistedBefore;
        private static string? _crashBackup;
        private static bool _crashExistedBefore;

        private static AppSession? _session;
        private static string? _launchError;

        private static readonly TimeSpan T = TimeSpan.FromSeconds(10);

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            _profileExistedBefore = File.Exists(ProfilePath);
            if (_profileExistedBefore) _profileBackup = File.ReadAllText(ProfilePath);

            _crashExistedBefore = File.Exists(CrashPath);
            if (_crashExistedBefore) _crashBackup = File.ReadAllText(CrashPath);

            Directory.CreateDirectory(SettingsDir);
            if (File.Exists(CrashPath)) File.Delete(CrashPath);
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
                if (_profileExistedBefore) File.WriteAllText(ProfilePath, _profileBackup!);
                else if (File.Exists(ProfilePath)) File.Delete(ProfilePath);

                // Свежий отчёт о сбое, если он появился ПО ХОДУ прогона, не
                // затираем: это результат теста, его надо увидеть.
                if (!File.Exists(CrashPath) && _crashExistedBefore)
                    File.WriteAllText(CrashPath, _crashBackup!);
            }
            catch { /* гонка с диском сразу после Kill() не должна маскировать результат */ }
        }

        private static AppSession Require()
        {
            if (_session == null) Assert.Inconclusive($"Клиент не запустился: {_launchError}");
            return _session!;
        }

        // ── помощники ────────────────────────────────────────────────────────

        private static void OpenCatalog(AppSession s)
        {
            var catalogBtn = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCatalogTab")),
                timeout: T, throwOnTimeout: false).Result;
            Assert.IsNotNull(catalogBtn, "Не найдена вкладка «Каталог».");
            catalogBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(1200);
        }

        private static Window OpenCodeDialog(AppSession s)
        {
            var btn = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnPresetByCode")),
                timeout: T, throwOnTimeout: false).Result;
            Assert.IsNotNull(btn, "Не найдена кнопка «Набор с сайта».");
            btn!.AsButton().Invoke();

            var dialog = Retry.WhileNull(() => s.MainWindow.ModalWindows.FirstOrDefault(),
                timeout: T, interval: TimeSpan.FromMilliseconds(200), throwOnTimeout: false).Result;
            Assert.IsNotNull(dialog, "Диалог кода набора не открылся.");
            return dialog!;
        }

        private static void TypeCode(Window dialog, string code)
        {
            var box = dialog.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
            Assert.IsNotNull(box, "В диалоге нет поля для кода.");
            box!.AsTextBox().Text = code;
            System.Threading.Thread.Sleep(200);
        }

        private static void ClickConfirm(Window dialog)
        {
            var ok = dialog.FindFirstDescendant(cf => cf.ByAutomationId("btnOk"));
            Assert.IsNotNull(ok, "В диалоге нет кнопки «Отметить».");
            ok!.AsButton().Invoke();
            System.Threading.Thread.Sleep(700);
        }

        /// <summary>
        /// Список каталога виртуализирован: строки существуют в дереве
        /// автоматизации только пока видны на экране. Поэтому перед проверкой
        /// конкретного приложения сужаем список поиском — иначе элемент просто
        /// «не находится», и это легко принять за незаполненный чекбокс.
        /// </summary>
        private static void FilterCatalog(AppSession s, string query)
        {
            var search = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("txtSearch")),
                timeout: T, throwOnTimeout: false).Result;
            Assert.IsNotNull(search, "Не найдено поле поиска по каталогу.");
            search!.AsTextBox().Text = query;
            System.Threading.Thread.Sleep(900);
        }

        private static bool? IsAppChecked(AppSession s, string appId, string query)
        {
            FilterCatalog(s, query);
            var box = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId($"chkApp_{appId}")),
                timeout: TimeSpan.FromSeconds(5), interval: TimeSpan.FromMilliseconds(200),
                throwOnTimeout: false).Result;
            Assert.IsNotNull(box, $"Строка приложения {appId} не найдена даже после фильтрации поиском по «{query}».");
            return box!.AsCheckBox().IsChecked;
        }

        private static void SetChecked(AppSession s, string appId, string query, bool value)
        {
            FilterCatalog(s, query);
            var box = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId($"chkApp_{appId}")),
                timeout: TimeSpan.FromSeconds(5), interval: TimeSpan.FromMilliseconds(200),
                throwOnTimeout: false).Result;
            if (box != null && box.AsCheckBox().IsChecked != value) box.AsCheckBox().IsChecked = value;
            System.Threading.Thread.Sleep(300);
        }

        /// <summary>Закрывает MessageBox, если он всплыл: иначе повиснет весь класс тестов.</summary>
        private static string CloseMessageBoxIfAny(AppSession s)
        {
            // Ищем и среди модальных детей главного окна, и среди верхнеуровневых
            // окон рабочего стола: MessageBox не всегда числится дочерним.
            var mb = Retry.WhileNull(
                () => s.MainWindow.ModalWindows.FirstOrDefault(w => w.Title.Contains("Набор"))
                      ?? s.Automation.GetDesktop()
                          .FindAllChildren(cf => cf.ByControlType(ControlType.Window))
                          .FirstOrDefault(w => w.Name.Contains("Набор") && !w.Name.Contains("Набор с сайта"))
                          ?.AsWindow(),
                timeout: TimeSpan.FromSeconds(5), interval: TimeSpan.FromMilliseconds(250),
                throwOnTimeout: false).Result;
            if (mb == null) return "";

            string text = string.Join(" | ", mb.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                                                .Select(t => t.Name));
            var ok = mb.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button));
            ok?.AsButton().Invoke();
            System.Threading.Thread.Sleep(400);
            return text;
        }

        // ── тесты ────────────────────────────────────────────────────────────

        [TestMethod]
        public void НаборСайта_КнопкаОткрываетДиалогИЗакрываетсяОтменой()
        {
            var s = Require();
            OpenCatalog(s);
            var dialog = OpenCodeDialog(s);

            StringAssert.Contains(dialog.Title, "Набор с сайта",
                "Заголовок диалога не тот, что ожидался.");

            var cancel = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                               .FirstOrDefault(b => b.Name.Contains("Отмена"));
            Assert.IsNotNull(cancel, "В диалоге нет кнопки «Отмена».");
            cancel!.AsButton().Invoke();
            System.Threading.Thread.Sleep(500);

            Assert.AreEqual(0, s.MainWindow.ModalWindows.Length,
                "После «Отмены» диалог должен закрыться.");
        }

        [TestMethod]
        public void НаборСайта_ВалидныйКодОтмечаетИменноУказанныеПриложения()
        {
            var s = Require();
            OpenCatalog(s);
            SetChecked(s, "vlc", "vlc", false);
            SetChecked(s, "telegram", "telegram", false);
            SetChecked(s, "7zip", "7-zip", false);

            var dialog = OpenCodeDialog(s);
            TypeCode(dialog, "V4T:vlc,telegram");
            ClickConfirm(dialog);
            CloseMessageBoxIfAny(s);

            Assert.AreEqual(true, IsAppChecked(s, "vlc", "vlc"), "VLC не отмечен по коду набора.");
            Assert.AreEqual(true, IsAppChecked(s, "telegram", "telegram"), "Telegram не отмечен по коду набора.");
            // Ключевая проверка: отмечено РОВНО то, что просили, а не «всё подряд».
            Assert.AreEqual(false, IsAppChecked(s, "7zip", "7-zip"),
                "7-Zip отмечен, хотя его не было в коде набора.");

            SetChecked(s, "vlc", "vlc", false);
            SetChecked(s, "telegram", "telegram", false);
        }

        [TestMethod]
        public void НаборСайта_СсылкаССайтаПринимаетсяНарядуСКодом()
        {
            var s = Require();
            OpenCatalog(s);
            SetChecked(s, "google-chrome", "chrome", false);
            SetChecked(s, "vlc", "vlc", false);

            var dialog = OpenCodeDialog(s);
            TypeCode(dialog, "https://ven4tools.ru/?scene=catalog&set=google-chrome");
            ClickConfirm(dialog);
            CloseMessageBoxIfAny(s);

            Assert.AreEqual(true, IsAppChecked(s, "google-chrome", "chrome"),
                "Приложение из вставленной ссылки не отмечено.");
            Assert.AreEqual(false, IsAppChecked(s, "vlc", "vlc"),
                "Отмечено лишнее приложение, которого не было в ссылке.");

            SetChecked(s, "google-chrome", "chrome", false);
        }

        [TestMethod]
        public void НаборСайта_НепригодныйКодНеЗакрываетОкноИОбъясняетПричину()
        {
            var s = Require();
            OpenCatalog(s);
            var dialog = OpenCodeDialog(s);

            TypeCode(dialog, "просто какой-то текст");
            ClickConfirm(dialog);

            // Окно обязано остаться открытым: иначе человек потеряет вставленное.
            Assert.AreEqual(1, s.MainWindow.ModalWindows.Length,
                "Диалог закрылся на непригодном коде — вставленный текст потерян.");

            string hints = string.Join(" ", dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                                                  .Select(t => t.Name));
            StringAssert.Contains(hints, "V4T:",
                "Подсказка не объясняет, как выглядит правильный код.");

            var cancel = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                               .FirstOrDefault(b => b.Name.Contains("Отмена"));
            cancel!.AsButton().Invoke();
            System.Threading.Thread.Sleep(400);
        }

        [TestMethod]
        public void НаборСайта_НеизвестноеПриложениеНеМолчаТеряется()
        {
            var s = Require();
            OpenCatalog(s);
            SetChecked(s, "vlc", "vlc", false);

            var dialog = OpenCodeDialog(s);
            // Несуществующий id намеренно ЛАТИНИЦЕЙ: кириллицу парсер законно
            // отбрасывает как непригодный идентификатор ещё до сверки с
            // каталогом, и сообщения о ненайденном тогда просто не будет.
            TypeCode(dialog, "V4T:vlc,no-such-app-xyz");
            ClickConfirm(dialog);

            // Человек должен узнать, что отмечено не всё, иначе решит, что
            // поставил весь набор.
            string text = CloseMessageBoxIfAny(s);
            Assert.AreNotEqual("", text,
                "О ненайденном приложении не сообщено — набор молча отмечен частично.");
            StringAssert.Contains(text, "no-such-app-xyz",
                "В сообщении не указано, какое именно приложение не найдено.");

            Assert.AreEqual(true, IsAppChecked(s, "vlc", "vlc"),
                "Годное приложение из того же кода должно быть отмечено.");

            SetChecked(s, "vlc", "vlc", false);
        }

        [TestMethod]
        public void НаборСайта_ОтменаНеМеняетОтметки()
        {
            var s = Require();
            OpenCatalog(s);
            SetChecked(s, "obs-studio", "obs", false);

            var dialog = OpenCodeDialog(s);
            TypeCode(dialog, "V4T:obs-studio");

            var cancel = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                               .FirstOrDefault(b => b.Name.Contains("Отмена"));
            cancel!.AsButton().Invoke();
            System.Threading.Thread.Sleep(500);

            Assert.AreEqual(false, IsAppChecked(s, "obs-studio", "obs"),
                "«Отмена» всё равно отметила приложение.");
        }
    }
}
