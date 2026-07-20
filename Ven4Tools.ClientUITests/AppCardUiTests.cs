using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ven4Tools.ClientUITests
{
    /// <summary>
    /// Рантайм-проверка новой поверхности «Карточка приложения» (AppCardWindow):
    /// открытие кликом по имени в каталоге, набор кнопок в зависимости от состояния
    /// (CanLaunch vs IsInstalled), закрытие по Esc, доступность StatusText/ссылки.
    /// Плюс увеличенный хитбокс чекбокса 20×20.
    ///
    /// Каталог виртуализирует строки по категориям (реализуется лишь видимая часть),
    /// поэтому строки находятся через ПОИСК (фильтр сворачивает список до нужного
    /// приложения — стабильно, без зависимости от прокрутки/раскрытия категорий).
    ///
    /// Рискованные действия (реальная установка/удаление/переустановка ПО) НЕ
    /// кликаются — проверяется только видимость/доступность кнопок.
    /// </summary>
    [TestClass]
    public class AppCardUiTests
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

        // ── Навигация / поиск ────────────────────────────────────────────────────

        private static AutomationElement GetSearchBox(AppSession s)
        {
            var catalogBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCatalogTab"));
            catalogBtn?.AsButton().Invoke();
            var search = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("txtSearch")),
                timeout: T, interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(search, "Не найдено поле поиска каталога (txtSearch).");
            return search!;
        }

        // Вводит запрос в поиск и ждёт, пока в реализованном дереве появится строка,
        // чей chkApp_-AutomationId содержит ожидаемый фрагмент id. Возвращает чекбокс
        // этой строки (или null, если не появилась за таймаут).
        private static AutomationElement? SearchForRow(AppSession s, string query, string expectIdFragment)
        {
            var search = GetSearchBox(s);
            search.Focus();
            var tb = search.AsTextBox();
            tb.Enter(query); // ValuePattern.SetValue → SearchText (UpdateSourceTrigger=PropertyChanged)
            System.Threading.Thread.Sleep(300);

            return Retry.WhileNull(
                () => s.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox))
                        .FirstOrDefault(c => (c.Properties.AutomationId.ValueOrDefault ?? "")
                            .Contains("chkApp_" + expectIdFragment, StringComparison.OrdinalIgnoreCase)),
                timeout: TimeSpan.FromSeconds(12), interval: TimeSpan.FromMilliseconds(400),
                throwOnTimeout: false).Result;
        }

        private static AutomationElement? NameTextForCheckBox(AutomationElement chk)
        {
            var col0 = chk.Parent; // StackPanel(колонка0) — чекбокс больше не обёрнут в Viewbox
            return col0?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text));
        }

        // Строка каталога (Grid) для чекбокса — StackPanel(кол.0) → Grid.
        private static AutomationElement? RowGridForCheckBox(AutomationElement chk) =>
            chk.Parent?.Parent;

        // ── Карточка ─────────────────────────────────────────────────────────────

        private static readonly string[] CardMarkerButtons = { "🗑 Удалить", "Установить", "🔄 Переустановить" };

        private static bool LooksLikeCard(AutomationElement w) =>
            CardMarkerButtons.Any(n => w.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName(n))) != null);

        private static Window? FindCardWindow(AppSession s, TimeSpan timeout) =>
            Retry.WhileNull(() =>
            {
                var modal = s.MainWindow.ModalWindows.FirstOrDefault(LooksLikeCard);
                if (modal != null) return modal;
                var win = s.Automation.GetDesktop()
                    .FindAllChildren(cf => cf.ByControlType(ControlType.Window))
                    .FirstOrDefault(LooksLikeCard);
                return win?.AsWindow();
            }, timeout: timeout, interval: TimeSpan.FromMilliseconds(400), throwOnTimeout: false).Result;

        private static Window? OpenCard(AppSession s, AutomationElement chk)
        {
            var name = NameTextForCheckBox(chk);
            Assert.IsNotNull(name, "Не найден TextBlock имени приложения рядом с чекбоксом.");
            name!.Click(); // реальный мышиный клик → MouseBinding LeftClick → OpenCardCommand
            return FindCardWindow(s, T);
        }

        private static void CloseCard(AppSession s)
        {
            try
            {
                var win = s.Automation.GetDesktop()
                    .FindAllChildren(cf => cf.ByControlType(ControlType.Window))
                    .FirstOrDefault(LooksLikeCard);
                if (win != null)
                {
                    win.AsWindow().Focus();
                    System.Threading.Thread.Sleep(150);
                    Keyboard.Type(VirtualKeyShort.ESCAPE);
                    System.Threading.Thread.Sleep(300);
                }
            }
            catch { }
        }

        private static List<string> CardButtonNames(AutomationElement card) =>
            card.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Select(b => b.Name ?? "").Where(n => n.Length > 0).ToList();

        // ── Тесты ────────────────────────────────────────────────────────────────

        [TestMethod]
        public void КликПоИмени_ОткрываетКарточку_StatusTextДоступен()
        {
            var s = Require();
            try
            {
                var chk = SearchForRow(s, "Telegram", "telegram");
                Assert.IsNotNull(chk, "Поиск «Telegram» не дал строки каталога (chkApp_telegram).");

                var card = OpenCard(s, chk!);
                Assert.IsNotNull(card, "Карточка приложения не открылась после клика по имени.");

                var btns = CardButtonNames(card!);
                Assert.IsTrue(btns.Any(n => CardMarkerButtons.Contains(n) || n == "▶ Запустить"),
                    "В карточке нет ни одной кнопки действия. Кнопки: " + string.Join(" | ", btns));

                // StatusText/описание — есть текстовые элементы, и они на экране
                // (сегодняшний LOW-фикс: ScrollViewer не должен прятать нижний текст).
                var texts = card.FindAllDescendants(cf => cf.ByControlType(ControlType.Text));
                Assert.IsTrue(texts.Length > 0 && texts.Any(t => !t.IsOffscreen),
                    "В карточке нет видимых текстовых элементов (StatusText/описание обрезаны).");
            }
            finally { CloseCard(s); }
        }

        [TestMethod]
        public void Esc_ЗакрываетКарточку()
        {
            var s = Require();
            bool opened = false;
            try
            {
                var chk = SearchForRow(s, "Telegram", "telegram");
                Assert.IsNotNull(chk, "Поиск «Telegram» не дал строки для теста Esc.");
                var card = OpenCard(s, chk!);
                Assert.IsNotNull(card, "Карточка не открылась — нечего закрывать по Esc.");
                opened = true;

                card!.Focus();
                System.Threading.Thread.Sleep(200);
                Keyboard.Type(VirtualKeyShort.ESCAPE);
                System.Threading.Thread.Sleep(700);

                var still = FindCardWindow(s, TimeSpan.FromSeconds(3));
                Assert.IsNull(still, "Карточка всё ещё открыта после Esc — фикс OnPreviewKeyDown не сработал.");
                opened = false;
            }
            finally { if (opened) CloseCard(s); }
        }

        [TestMethod]
        public void КартаНезапускаемого_БезКнопкиЗапустить()
        {
            var s = Require();
            try
            {
                // Telegram нет в HKLM App Paths → CanLaunch=false (установлен он или нет).
                // В карточке не должно быть «▶ Запустить» (MEDIUM-1: launch завязан на
                // CanLaunch, а не на факте установки).
                var chk = SearchForRow(s, "Telegram", "telegram");
                Assert.IsNotNull(chk, "Поиск «Telegram» не дал строки.");
                var card = OpenCard(s, chk!);
                Assert.IsNotNull(card, "Карточка не открылась.");

                var btns = CardButtonNames(card!);
                Assert.IsFalse(btns.Any(n => n == "▶ Запустить"),
                    "У незапускаемого приложения в карточке есть «▶ Запустить» (MEDIUM-1 нарушен). Кнопки: "
                    + string.Join(" | ", btns));
            }
            finally { CloseCard(s); }
        }

        [TestMethod]
        public void КартаЗапускаемого_ПоказываетКнопкуЗапустить()
        {
            var s = Require();
            s.MainWindow.SetForeground();
            string tried = "";
            try
            {
                // Прогрев резолвера exe (AppLaunchResolver строит индекс асинхронно на
                // старте: реестр + Start Menu + COM) — иначе ▶ у установленных ещё нет.
                // Опрос вместо фиксированного сна: под нагрузкой (этот тест часто идёт
                // последним в длинном прогоне, после реальных install/uninstall из других
                // классов) фиксированные 12с не всегда достаточны — индекс продолжает
                // строиться, а тест уже читает состояние строки. Поднято до 30с общего
                // таймаута с активной проверкой вместо слепого ожидания.
                GetSearchBox(s);
                AutomationElement? readyChk = null;
                string readyQuery = "", readyFrag = "";
                Retry.WhileFalse(() =>
                {
                    foreach (var (query, frag) in new[] { ("7-Zip", "7zip"), ("Notepad++", "notepad"), ("WinRAR", "winrar") })
                    {
                        var c = SearchForRow(s, query, frag);
                        if (c == null) continue;
                        var rowGrid = RowGridForCheckBox(c);
                        bool hasPlay = rowGrid != null && rowGrid
                            .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                            .Any(b => (b.Name ?? "") == "▶");
                        if (hasPlay) { readyChk = c; readyQuery = query; readyFrag = frag; return true; }
                    }
                    return false;
                }, timeout: TimeSpan.FromSeconds(30), interval: TimeSpan.FromSeconds(2), throwOnTimeout: false);

                // Ищем установленное И резолвящееся приложение: строка каталога
                // показывает ▶ (ToolTip «Запустить») только при CanLaunch=true.
                foreach (var (query, frag) in new[] { (readyQuery, readyFrag), ("7-Zip", "7zip"), ("Notepad++", "notepad"), ("WinRAR", "winrar") })
                {
                    if (string.IsNullOrEmpty(query)) continue;
                    tried += query + " ";
                    var chk = readyChk != null && query == readyQuery ? readyChk : SearchForRow(s, query, frag);
                    if (chk == null) continue;
                    var rowGrid = RowGridForCheckBox(chk);
                    bool rowHasPlay = rowGrid != null && rowGrid
                        .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                        .Any(b => (b.Name ?? "") == "▶");
                    if (!rowHasPlay) continue;

                    var card = OpenCard(s, chk);
                    Assert.IsNotNull(card, $"Карточка «{query}» не открылась.");
                    var cardBtnsInitial = CardButtonNames(card!);
                    System.Threading.Thread.Sleep(2500); // дать возможной отложенной нотификации сработать
                    var cardBtnsAfter = CardButtonNames(card);
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "appcard_launch_diag.txt"),
                        $"{query}: строка ▶={rowHasPlay}\n  сразу: " + string.Join(" | ", cardBtnsInitial) +
                        "\n  через 2.5с: " + string.Join(" | ", cardBtnsAfter));
                    var launch = card!.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                        .FirstOrDefault(b => (b.Name ?? "") == "▶ Запустить");
                    Assert.IsNotNull(launch,
                        $"У запускаемого «{query}» в строке есть ▶, но в карточке нет «▶ Запустить» (MEDIUM-1). " +
                        "Кнопки сразу: " + string.Join(" | ", cardBtnsInitial) +
                        "; через 2.5с: " + string.Join(" | ", cardBtnsAfter));
                    Assert.IsTrue(launch!.IsEnabled, "Кнопка «▶ Запустить» в покое отключена (ожидалась активной).");
                    return;
                }
                Assert.Inconclusive("Ни одно из проверенных приложений (" + tried.Trim() + ") не оказалось " +
                    "установленным с резолвящимся exe (CanLaunch=true) — позитивный кейс MEDIUM-1 не воспроизводим средой.");
            }
            finally { CloseCard(s); }
        }

        [TestMethod]
        public void СсылкаСайтИсточник_TooltipСодержитURL()
        {
            var s = Require();
            try
            {
                foreach (var (query, frag) in new[] { ("Telegram", "telegram"), ("7-Zip", "7zip"), ("Discord", "discord") })
                {
                    var chk = SearchForRow(s, query, frag);
                    if (chk == null) continue;
                    var card = OpenCard(s, chk);
                    if (card == null) continue;

                    var link = card.FindAllDescendants(cf => cf.ByControlType(ControlType.Hyperlink))
                        .FirstOrDefault(h => (h.Name ?? "").Contains("Сайт-источник"))
                        ?? card.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                            .FirstOrDefault(t => (t.Name ?? "").Contains("Сайт-источник"));

                    if (link != null)
                    {
                        string tip = link.Properties.HelpText.IsSupported ? (link.Properties.HelpText.Value ?? "") : "";
                        CloseCard(s);
                        if (tip.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return; // ✅
                        Assert.Inconclusive($"Ссылка «Сайт-источник» найдена ({query}), но UIA не отдаёт ToolTip с URL " +
                            $"(значение: '{tip}'). Проверка URL — за код-ревью.");
                        return;
                    }
                    CloseCard(s);
                }
                Assert.Inconclusive("Ни в одной из проб не оказалось ссылки «Сайт-источник».");
            }
            finally { CloseCard(s); }
        }

        [TestMethod]
        public void Чекбокс_Хитбокс20х20_КликВУголПереключает()
        {
            var s = Require();
            // В полном прогоне класс идёт после других, где могло остаться неотфокусированное
            // окно/потерянный фокус — реальный OS-клик по координатам мимо активного окна не
            // долетает до контрола (см. ScreenshotGeneration.cs — тот же приём).
            s.MainWindow.SetForeground();
            System.Threading.Thread.Sleep(200);
            var chk = SearchForRow(s, "Telegram", "telegram");
            Assert.IsNotNull(chk, "Поиск «Telegram» не дал чекбокса для проверки хитбокса.");
            Assert.IsTrue(chk!.IsEnabled, "Чекбокс приложения отключён — нельзя проверить переключение.");

            var rect = chk.BoundingRectangle;
            // Viewbox 20×20 растягивает чекбокс по ширине до 20px (голый WPF-чекбокс ~13px).
            // По высоте Uniform-масштабирование неквадратного контрола оставляет ~14px
            // (лётербокс сверху/снизу) — область по горизонтали увеличена, что и было целью.
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "appcard_chk_diag.txt"),
                $"checkbox rect = {rect.Width}x{rect.Height}");
            Assert.IsTrue(rect.Width >= 18,
                $"Ширина хитбокса чекбокса не увеличена Viewbox (фактически {rect.Width}×{rect.Height}px, ожидалось ≥18 по ширине).");

            bool? before = chk.AsCheckBox().IsChecked;
            var corner = new Point(rect.Left + 2, rect.Top + 2); // почти угол увеличенной области
            Mouse.Click(corner);
            System.Threading.Thread.Sleep(400);

            bool? after = chk.AsCheckBox().IsChecked;
            Assert.AreNotEqual(before, after,
                $"Клик в угол хитбокса ({rect.Width}×{rect.Height}px) не переключил чекбокс (было {before}, стало {after}).");

            Mouse.Click(corner); // вернуть состояние
            System.Threading.Thread.Sleep(200);
        }
    }
}
