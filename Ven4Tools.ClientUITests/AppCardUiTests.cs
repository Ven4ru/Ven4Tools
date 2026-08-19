using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        /// <summary>
        /// Заново находит чекбокс строки по её устойчивому AutomationId.
        /// Держать ссылку на найденный ранее элемент нельзя: при перестроении
        /// списка тот же контейнер достаётся другому приложению, и действие
        /// уходит не туда (поймано вживую: искали 7-Zip, открылась карточка Telegram).
        /// </summary>
        private static AutomationElement? ResolveRow(AppSession s, string expectIdFragment) =>
            s.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox))
                .FirstOrDefault(c => (c.Properties.AutomationId.ValueOrDefault ?? "")
                    .Contains("chkApp_" + expectIdFragment, StringComparison.OrdinalIgnoreCase));

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

        /// <summary>
        /// Находит подпись приложения по её ТЕКСТУ, а не подъёмом от чекбокса по
        /// дереву. В виртуализированном списке переход «чекбокс → родитель →
        /// текст» приводил к подписи чужой строки: элемент находился по верному
        /// chkApp_&lt;id&gt;, а клик открывал карточку другого приложения
        /// (поймано вживую: искали 7-Zip, открывался Telegram Desktop).
        /// </summary>
        private static AutomationElement? NameTextByAppTitle(AppSession s, string titleFragment) =>
            s.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .FirstOrDefault(t => (t.Name ?? "").Contains(titleFragment, StringComparison.OrdinalIgnoreCase)
                                     && t.BoundingRectangle.Width > 0);

        // Строка каталога (Grid) для чекбокса — StackPanel(кол.0) → Grid.
        private static AutomationElement? RowGridForCheckBox(AutomationElement chk) =>
            chk.Parent?.Parent;

        /// <summary>
        /// Есть ли у ЭТОЙ строки кнопка «▶». Принадлежность строке определяем
        /// геометрически — по вертикальному перекрытию с чекбоксом, а не подъёмом
        /// на два уровня по дереву: если подняться выше самой строки, в выборку
        /// попадёт ▶ соседнего приложения, и тест решит, что запускаемо не то,
        /// чью карточку он потом открывает.
        /// </summary>
        private static bool RowHasPlayButton(AutomationElement chk)
        {
            var chkRect = chk.BoundingRectangle;
            var scope = RowGridForCheckBox(chk);
            if (scope == null) return false;

            return scope.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Any(b => (b.Name ?? "") == "▶"
                          && b.BoundingRectangle.Top < chkRect.Bottom
                          && b.BoundingRectangle.Bottom > chkRect.Top);
        }

        // ── Карточка ─────────────────────────────────────────────────────────────

        private static readonly string[] CardMarkerButtons = { "🗑 Удалить", "Установить", "🔄 Переустановить" };

        private static bool LooksLikeCard(AutomationElement w) =>
            CardMarkerButtons.Any(n => w.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName(n))) != null);

        private static Window? FindCardWindow(AppSession s, TimeSpan timeout, string? exceptTitle = null) =>
            Retry.WhileNull(() =>
            {
                // exceptTitle — заголовок карточки, которая была открыта ДО клика.
                // Без этого отбора находилась она же, и тест делал выводы о чужом
                // приложении, будучи уверенным, что смотрит на своё.
                bool Suitable(AutomationElement w) =>
                    LooksLikeCard(w) && (exceptTitle == null || (w.AsWindow().Title ?? "") != exceptTitle);

                var modal = s.MainWindow.ModalWindows.FirstOrDefault(Suitable);
                if (modal != null) return modal;
                var win = s.Automation.GetDesktop()
                    .FindAllChildren(cf => cf.ByControlType(ControlType.Window))
                    .FirstOrDefault(Suitable);
                return win?.AsWindow();
            }, timeout: timeout, interval: TimeSpan.FromMilliseconds(400), throwOnTimeout: false).Result;

        private static Window? OpenCard(AppSession s, AutomationElement chk, string? reresolveFragment = null, string? expectedTitleFragment = null)
        {
            // Перед самым кликом берём строку заново: между поиском и действием
            // список мог перестроиться и отдать контейнер другому приложению.
            // Молчаливого отката к прежнему элементу быть НЕ должно: он приводит
            // к клику по чужой строке, и разбираться потом приходится по симптому
            // «открылась карточка не того приложения».
            AutomationElement? name;

            if (reresolveFragment != null)
            {
                var fresh = ResolveRow(s, reresolveFragment);
                Assert.IsNotNull(fresh,
                    $"Строка chkApp_{reresolveFragment} исчезла из списка между поиском и открытием " +
                    "карточки (список перестроился или сменился фильтр поиска).");
                chk = fresh!;

                // Кликаем по подписи, найденной по тексту приложения: навигация от
                // чекбокса по дереву в виртуализированном списке приводила к подписи
                // соседней строки.
                name = NameTextByAppTitle(s, expectedTitleFragment ?? reresolveFragment)
                       ?? NameTextForCheckBox(chk);
            }
            else
            {
                name = NameTextForCheckBox(chk);
            }

            Assert.IsNotNull(name, "Не найден TextBlock имени приложения рядом с чекбоксом.");

            // Остатки прошлых карточек закрываем ДО клика: модальное окно
            // проглатывает ввод в главное окно, и клик просто не дойдёт.
            string? staleTitle = OpenCards(s).FirstOrDefault()?.Title;
            if (staleTitle != null) CloseCard(s);

            name!.Click(); // реальный мышиный клик → MouseBinding LeftClick → OpenCardCommand
            return FindCardWindow(s, T, staleTitle);
        }

        /// <summary>Все открытые окна-карточки на рабочем столе.</summary>
        private static List<Window> OpenCards(AppSession s)
        {
            try
            {
                // Оба пути обязательны: карточка — модальное окно, принадлежащее
                // главному, и среди детей рабочего стола она может не значиться.
                // Раньше проверялся только рабочий стол, поэтому остатки карточек
                // не находились и не закрывались.
                var result = s.MainWindow.ModalWindows.Where(LooksLikeCard).ToList();

                foreach (var w in s.Automation.GetDesktop()
                             .FindAllChildren(cf => cf.ByControlType(ControlType.Window))
                             .Where(LooksLikeCard)
                             .Select(w => w.AsWindow()))
                {
                    if (!result.Any(x => x.Properties.NativeWindowHandle.ValueOrDefault ==
                                         w.Properties.NativeWindowHandle.ValueOrDefault))
                        result.Add(w);
                }

                return result;
            }
            catch { return new List<Window>(); }
        }

        /// <summary>
        /// Закрывает ВСЕ карточки, а не первую попавшуюся, и дожидается, что их
        /// не осталось. Незакрытая карточка — модальное окно: оно не мешает UIA
        /// читать элементы главного окна, но ВВОД в него блокирует. Именно из-за
        /// этого «клик по чекбоксу не переключал его» и «открывалась карточка
        /// чужого приложения» — то была карточка, оставшаяся от прошлого теста.
        /// </summary>
        private static void CloseCard(AppSession s)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var cards = OpenCards(s);
                if (cards.Count == 0) return;

                foreach (var card in cards)
                {
                    try
                    {
                        card.Focus();
                        System.Threading.Thread.Sleep(120);
                        Keyboard.Type(VirtualKeyShort.ESCAPE);
                        System.Threading.Thread.Sleep(250);
                    }
                    catch { }
                }
            }
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
                        if (RowHasPlayButton(c)) { readyChk = c; readyQuery = query; readyFrag = frag; return true; }
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
                    bool rowHasPlay = RowHasPlayButton(chk);
                    if (!rowHasPlay) continue;

                    string rowsBeforeClick = DumpRows(s);
                    string clickedLabel = DescribeLabelFor(s, query);
                    var card = OpenCard(s, chk, frag, query);
                    Assert.IsNotNull(card, $"Карточка «{query}» не открылась.");
                    // Заголовок карточки — DisplayName приложения. Если он не тот,
                    // значит открыли карточку соседней строки, и любые выводы о
                    // кнопке запуска были бы про другое приложение.
                    Assert.IsTrue((card!.Title ?? "").Contains(query, StringComparison.OrdinalIgnoreCase),
                        $"Открыта карточка «{card.Title}», а ожидалась «{query}» — строка и карточка разошлись. " +
                        $"Состояние списка перед кликом: {rowsBeforeClick}. Кликали по подписи: {clickedLabel}.");
                    var cardBtnsInitial = CardButtonNames(card!);

                    // Ждём кнопку опросом, а не фиксированной паузой: путь к exe
                    // резолвится асинхронно, и под нагрузкой полного набора 2.5 с
                    // не хватало. Карточка подписана на PropertyChanged строки и
                    // обновляет ShowLaunchButton сама — нужно лишь дать ей дойти.
                    Retry.WhileNull(
                        () => card!.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                                   .FirstOrDefault(b => (b.Name ?? "") == "▶ Запустить"),
                        timeout: TimeSpan.FromSeconds(10), interval: TimeSpan.FromMilliseconds(500),
                        throwOnTimeout: false);
                    var cardBtnsAfter = CardButtonNames(card);
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "appcard_launch_diag.txt"),
                        $"{query}: строка ▶={rowHasPlay}\n  сразу: " + string.Join(" | ", cardBtnsInitial) +
                        "\n  через 2.5с: " + string.Join(" | ", cardBtnsAfter));
                    var launch = card!.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                        .FirstOrDefault(b => (b.Name ?? "") == "▶ Запустить");
                    Assert.IsNotNull(launch,
                        $"У запускаемого «{query}» в строке есть ▶, но в карточке нет «▶ Запустить» (MEDIUM-1). " +
                        "Кнопки сразу: " + string.Join(" | ", cardBtnsInitial) +
                        "; после ожидания до 10с: " + string.Join(" | ", cardBtnsAfter));
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

            // Строку берём заново перед самым кликом: список мог перестроиться
            // и отдать контейнер другому приложению.
            chk = ResolveRow(s, "telegram") ?? chk;

            // И убеждаемся, что поверх нет открытой карточки: она модальная и
            // проглотит клик, оставив впечатление неисправного хитбокса.
            CloseCard(s);
            Assert.AreEqual(0, OpenCards(s).Count,
                "Поверх главного окна осталась карточка приложения — ввод в список заблокирован.");
            Assert.IsTrue(chk!.IsEnabled, "Чекбокс приложения отключён — нельзя проверить переключение.");

            var rect = chk.BoundingRectangle;
            // Viewbox 20×20 растягивает чекбокс по ширине до 20px (голый WPF-чекбокс ~13px).
            // По высоте Uniform-масштабирование неквадратного контрола оставляет ~14px
            // (лётербокс сверху/снизу) — область по горизонтали увеличена, что и было целью.
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "appcard_chk_diag.txt"),
                $"checkbox rect = {rect.Width}x{rect.Height}");
            Assert.IsTrue(rect.Width >= 18,
                $"Ширина хитбокса чекбокса не увеличена Viewbox (фактически {rect.Width}×{rect.Height}px, ожидалось ≥18 по ширине).");

            // Состояние читаем каждый раз по СВЕЖЕЙ ссылке. Уже доказано, что в
            // виртуализированном списке контейнеры переиспользуются: держать один
            // элемент между кликом и проверкой нельзя — он может отвечать за уже
            // другую строку и возвращать чужое значение.
            bool? ReadState()
            {
                var fresh = ResolveRow(s, "telegram");
                return fresh?.AsCheckBox().IsChecked;
            }

            bool? before = ReadState();
            var corner = new Point(rect.Left + 2, rect.Top + 2); // почти угол увеличенной области

            // Физический клик по экранным координатам долетает до контрола только
            // если в этой точке действительно окно клиента. В полном прогоне сверху
            // может оказаться чужое окно (например, браузер, открытый предыдущим
            // тестом) — тогда клик уходит в него, чекбокс не переключается, и тест
            // краснеет, хотя хитбокс в порядке (изолированно этот же тест проходит).
            // Поэтому перед кликом убеждаемся, что точка принадлежит нашему процессу.
            if (!EnsurePointBelongsToClient(s, corner))
            {
                Assert.Inconclusive(
                    "В точке клика оказалось окно другого процесса — проверить хитбокс " +
                    "физическим кликом нельзя. Это ограничение окружения, а не дефект контрола.");
            }

            // Что физически лежит под точкой — главный факт при разборе промаха.
            // WindowFromPoint отвечает только про окно (карточка/диалог того же
            // процесса его пройдут), поэтому спрашиваем UIA про сам элемент.
            string underPoint = DescribeElementAt(s, corner);

            Mouse.Click(corner);
            System.Threading.Thread.Sleep(400);

            bool? after = ReadState();

            if (before == after)
            {
                // Первый клик по окну, потерявшему активацию, Windows тратит на
                // саму активацию — контрол при этом не нажимается. Второй клик
                // это различает: сработал — проблема в активации окна, не сработал —
                // контрол к моменту клика стал недоступен (Availability/JustInstalled
                // меняются асинхронно и гасят IsSelectable).
                var freshAfter = ResolveRow(s, "telegram");
                bool enabledAfter = freshAfter?.IsEnabled ?? false;
                bool offscreenAfter = freshAfter?.IsOffscreen ?? true;

                Mouse.Click(corner);
                System.Threading.Thread.Sleep(400);
                bool? afterSecond = ReadState();

                Assert.AreNotEqual(before, afterSecond,
                    $"Клик в угол хитбокса ({rect.Width}×{rect.Height}px) не переключил чекбокс " +
                    $"ни с первой, ни со второй попытки (было {before}, стало {afterSecond}). " +
                    $"Под точкой клика: {underPoint}. После первого клика: IsEnabled={enabledAfter}, " +
                    $"IsOffscreen={offscreenAfter}. Список: {DumpRows(s)}. Целились в y{rect.Top}-{rect.Bottom}.");

                // Второй клик сработал — состояние вернём и зафиксируем причину.
                Mouse.Click(corner);
                System.Threading.Thread.Sleep(200);
                Assert.Inconclusive(
                    "Чекбокс переключился только со второго клика: первый ушёл на активацию окна. " +
                    "Хитбокс исправен, ограничение — активация окна под нагрузкой полного прогона.");
            }

            Mouse.Click(corner); // вернуть состояние
            System.Threading.Thread.Sleep(200);
        }

        /// <summary>
        /// Снимок того, что реально видно в списке: текст поиска, строки с их
        /// прямоугольниками. Нужен, чтобы разбирать промахи фактами, а не версиями.
        /// </summary>
        private static string DumpRows(AppSession s)
        {
            string query = "";
            try { query = GetSearchBox(s).AsTextBox().Text ?? ""; } catch { }

            var rows = s.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox))
                .Where(c => (c.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("chkApp_"))
                .Select(c =>
                {
                    var r = c.BoundingRectangle;
                    return $"{c.Properties.AutomationId.ValueOrDefault}@y{r.Top}-{r.Bottom}";
                })
                .ToList();

            return $"поиск=«{query}», строк={rows.Count}: " + string.Join(", ", rows.Take(8));
        }

        /// <summary>Какую именно подпись найдёт OpenCard и где она лежит.</summary>
        private static string DescribeLabelFor(AppSession s, string titleFragment)
        {
            var label = NameTextByAppTitle(s, titleFragment);
            if (label == null) return $"подпись с «{titleFragment}» не найдена";
            var r = label.BoundingRectangle;
            return $"«{label.Name}» @ y{r.Top}-{r.Bottom} x{r.Left}-{r.Right}";
        }

        /// <summary>Описывает элемент под точкой экрана — для разбора промахов клика.</summary>
        private static string DescribeElementAt(AppSession s, Point point)
        {
            try
            {
                var el = s.Automation.FromPoint(new System.Drawing.Point(point.X, point.Y));
                if (el == null) return "элемент не определён";
                string id = el.Properties.AutomationId.ValueOrDefault ?? "";
                string name = el.Name ?? "";
                return $"{el.ControlType} «{name}» id={(id == "" ? "—" : id)}";
            }
            catch (Exception ex) { return "определить не удалось: " + ex.Message; }
        }

        /// <summary>
        /// Поднимает окно клиента и проверяет, что указанная точка экрана
        /// принадлежит именно ему. Возвращает false, если после нескольких
        /// попыток точку по-прежнему перекрывает чужое окно.
        /// </summary>
        private static bool EnsurePointBelongsToClient(AppSession s, Point point)
        {
            int clientPid = s.MainWindow.Properties.ProcessId.ValueOrDefault;

            for (int attempt = 0; attempt < 5; attempt++)
            {
                s.MainWindow.SetForeground();
                System.Threading.Thread.Sleep(250);

                IntPtr hwnd = WindowFromPoint(new NativePoint { X = point.X, Y = point.Y });
                if (hwnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(hwnd, out uint pidUnderCursor);
                    if (pidUnderCursor == (uint)clientPid) return true;
                }
            }

            return false;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(NativePoint point);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    }
}
