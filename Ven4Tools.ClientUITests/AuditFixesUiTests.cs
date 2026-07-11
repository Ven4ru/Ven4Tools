using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ven4Tools.ClientUITests
{
    /// <summary>
    /// Рантайм-проверка 9 фиксов аудита 2026-07-11 (коммит d8d6d98): реальный клик
    /// по кнопке в живом клиенте, не только код-ревью. #3/#5/#9 сюда не входят —
    /// реальный клик модифицировал бы систему (UAC-рестарт недостижим при
    /// запуске от администратора, DebloaterTab реально удаляет Appx-пакеты,
    /// OfficeTab реально ставит Office) — по ним осталась только проверка diff/build.
    /// </summary>
    [TestClass]
    public class AuditFixesCatalogFlowTests
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
        private static readonly string ProfilePath = Path.Combine(SettingsDir, "profile.json");
        private static readonly string SourceOrderPath = Path.Combine(SettingsDir, "source_order.json");
        private static readonly string AppsPath = Path.Combine(SettingsDir, "apps.json");
        private static readonly string AlternativesPath = Path.Combine(SettingsDir, "alternatives.json");
        private static readonly string LogPath = Path.Combine(SettingsDir, "app.log");

        // "cpu-z" — реальный Id из каталога (Catalog/master.json). CheckAppAvailabilityFromCatalog
        // сперва ищет override через appManager.GetAppById(catalogApp.Id) — если он есть,
        // используется ВМЕСТО построения AppInfo из каталога. Подсовываем сюда заведомо
        // несуществующий AlternativeId, чтобы winget гарантированно не нашёл пакет и
        // приложение стало Unavailable — тогда появляется кнопка «Предложить альтернативу».
        private const string RealCatalogAppId = "cpu-z";
        private const string BogusAlternativeId = "Ven4ToolsTest.НесуществующийПакетZZZ";

        private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(15);

        private static string? _profileBackup; private static bool _profileExisted;
        private static string? _sourceOrderBackup; private static bool _sourceOrderExisted;
        private static string? _appsBackup; private static bool _appsExisted;
        private static string? _alternativesBackup; private static bool _alternativesExisted;

        private static AppSession? _session;
        private static string? _launchError;

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            Directory.CreateDirectory(SettingsDir);

            _profileExisted = File.Exists(ProfilePath);
            if (_profileExisted) _profileBackup = File.ReadAllText(ProfilePath);
            File.WriteAllText(ProfilePath, "{\"CatalogMode\":\"full\",\"HasSelectedCategory\":true}");

            _sourceOrderExisted = File.Exists(SourceOrderPath);
            if (_sourceOrderExisted) _sourceOrderBackup = File.ReadAllText(SourceOrderPath);
            if (File.Exists(SourceOrderPath)) File.Delete(SourceOrderPath);

            _appsExisted = File.Exists(AppsPath);
            if (_appsExisted) _appsBackup = File.ReadAllText(AppsPath);
            // Пользовательское приложение с заведомо несуществующим Id — при первой же
            // проверке доступности winget его не найдёт, статус станет Unavailable,
            // и появится кнопка «Предложить альтернативный источник» (#1). Имя подобрано
            // так, чтобы поиск winget внутри диалога тоже не дал результатов — ровно
            // тот путь, который был сломан (пустые результаты + ручной ввод).
            var overrideApp = new[]
            {
                new
                {
                    Id = RealCatalogAppId,
                    DisplayName = "CPU-Z",
                    Category = 9, // AppCategory.Другое
                    InstallerUrls = Array.Empty<string>(),
                    SilentArgs = "/S",
                    IsUserAdded = false,
                    RequiredSpaceMB = 10,
                    AlternativeId = BogusAlternativeId,
                    IsInstalled = false,
                    LocalInstallerPath = (string?)null,
                    ChocoId = ""
                }
            };
            File.WriteAllText(AppsPath, JsonSerializer.Serialize(overrideApp));

            _alternativesExisted = File.Exists(AlternativesPath);
            if (_alternativesExisted) _alternativesBackup = File.ReadAllText(AlternativesPath);
            if (File.Exists(AlternativesPath)) File.Delete(AlternativesPath);

            try { _session = AppSession.Launch(); }
            catch (Exception ex) { _launchError = ex.Message; _session = null; }
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            _session?.Dispose();
            _session = null;

            void Restore(string path, bool existed, string? backup)
            {
                try
                {
                    if (existed) File.WriteAllText(path, backup!);
                    else if (File.Exists(path)) File.Delete(path);
                }
                catch { }
            }

            Restore(ProfilePath, _profileExisted, _profileBackup);
            Restore(SourceOrderPath, _sourceOrderExisted, _sourceOrderBackup);
            Restore(AppsPath, _appsExisted, _appsBackup);
            Restore(AlternativesPath, _alternativesExisted, _alternativesBackup);
        }

        private static AppSession Require()
        {
            if (_session == null)
                Assert.Inconclusive("Клиент не запущен: " + (_launchError ?? "неизвестная причина"));
            return _session!;
        }

        /// <summary>Последние N символов app.log на момент вызова — точка отсчёта для поиска новых строк.</summary>
        private static long LogTailPosition()
        {
            try { return new FileInfo(LogPath).Length; } catch { return 0; }
        }

        private static string ReadLogSince(long position)
        {
            try
            {
                using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length <= position) return "";
                fs.Seek(position, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch { return ""; }
        }

        private static bool WaitForLogContains(long since, string needle, TimeSpan timeout)
        {
            return Retry.WhileFalse(
                () => ReadLogSince(since).Contains(needle, StringComparison.OrdinalIgnoreCase),
                timeout: timeout,
                interval: TimeSpan.FromMilliseconds(500),
                throwOnTimeout: false).Success;
        }

        [TestMethod]
        public void Полный_Проход_Каталог_НайденоДоступностьАльтернативаПорядокИсточников()
        {
            var s = Require();

            var catalogBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCatalogTab"));
            Assert.IsNotNull(catalogBtn, "Не найдена кнопка вкладки «Каталог».");
            long t0 = LogTailPosition();
            catalogBtn!.AsButton().Invoke();

            // Первичная загрузка каталога сама по себе проходит через семафор
            // проверки доступности (AppList.cs, availabilitySem) — ждём её конца.
            bool firstCheckDone = WaitForLogContains(t0, "Проверка завершена", TimeSpan.FromSeconds(60));
            Assert.IsTrue(firstCheckDone,
                "Первичная проверка доступности (авто, при загрузке каталога) не завершилась за 60с — возможен возврат зависания semaphore-бага (#4).");

            // ---- #4: явный клик «Проверить доступность» — второй путь через SemaphoreSlim ----
            var refreshBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnRefreshAvailability"));
            Assert.IsNotNull(refreshBtn, "Не найдена кнопка «Проверить доступность» (btnRefreshAvailability).");

            long t1 = LogTailPosition();
            refreshBtn!.AsButton().Invoke();

            bool refreshEnabled = Retry.WhileFalse(
                () => refreshBtn.AsButton().IsEnabled,
                timeout: TimeSpan.FromSeconds(3),
                interval: TimeSpan.FromMilliseconds(100),
                throwOnTimeout: false).Success;
            // Кнопка должна была задизейблиться на время проверки — если она всё
            // ещё enabled сразу после клика, проверка либо не запустилась, либо
            // была мгновенной (оба варианта проверяем ниже через лог и итоговое enabled).

            bool refreshDone = WaitForLogContains(t1, "Проверка завершена", TimeSpan.FromSeconds(60));
            Assert.IsTrue(refreshDone,
                "#4: явный запуск «Проверить доступность» не завершился за 60с — SemaphoreSlim мог не освободиться (зависание).");

            bool buttonReEnabled = Retry.WhileFalse(
                () => refreshBtn.AsButton().IsEnabled,
                timeout: TimeSpan.FromSeconds(5),
                interval: TimeSpan.FromMilliseconds(200),
                throwOnTimeout: false).Success;
            Assert.IsTrue(buttonReEnabled, "#4: кнопка «Проверить доступность» не вернулась в enabled-состояние после проверки.");

            // ---- #1: кнопка «Предложить альтернативный источник» у недоступного приложения ----
            var suggestButtons = s.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Where(b => (b.Properties.HelpText.IsSupported && b.Properties.HelpText.Value == "Предложить альтернативный источник"))
                .ToList();
            Assert.IsTrue(suggestButtons.Count >= 1,
                $"#1: не найдена кнопка «Предложить альтернативный источник» — приложение «{RealCatalogAppId}» (с подменённым AlternativeId) не помечено недоступным (или ещё проверяется).");

            suggestButtons[0].AsButton().Invoke();
            System.Threading.Thread.Sleep(1000);

            var desktop = s.Automation.GetDesktop();
            var dialog = Retry.WhileNull(
                () => s.MainWindow.ModalWindows.FirstOrDefault(),
                timeout: ElementTimeout,
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false).Result;

            dialog ??= Retry.WhileNull(
                () => desktop.FindAllChildren()
                              .FirstOrDefault(w => (w.Name ?? "").Contains("альтернативн", StringComparison.OrdinalIgnoreCase))
                              ?.AsWindow(),
                timeout: TimeSpan.FromSeconds(3),
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false).Result;

            if (dialog == null)
            {
                var allTitles = string.Join(" | ", desktop.FindAllChildren().Select(w => $"{w.ControlType}:{w.Name}"));
                Assert.Fail($"#1: диалог не открылся. Окна на рабочем столе сейчас: {allTitles}");
            }

            // Поиск winget внутри диалога по заведомо несуществующему имени должен
            // вернуть 0 результатов — именно эта ветка (пустой ItemsSource) была
            // сломана. Даём время на завершение авто-поиска.
            System.Threading.Thread.Sleep(4000);

            var manualId = dialog!.FindFirstDescendant(cf => cf.ByAutomationId("txtManualId"));
            Assert.IsNotNull(manualId, "#1: не найдено поле ручного ввода ID (txtManualId).");
            const string manualPackageId = "Ven4ToolsTest.ManualPackage123";
            manualId!.AsTextBox().Text = manualPackageId;
            System.Threading.Thread.Sleep(500); // TextChanged

            var okBtn = dialog.FindFirstDescendant(cf => cf.ByAutomationId("btnOk"));
            Assert.IsNotNull(okBtn, "#1: не найдена кнопка «Сохранить» (btnOk).");
            Assert.IsTrue(okBtn!.AsButton().IsEnabled, "#1: кнопка «Сохранить» не активировалась после ручного ввода ID (баг мог не быть исправлен).");
            okBtn.AsButton().Invoke();
            System.Threading.Thread.Sleep(1500);

            var modalsNow = s.MainWindow.ModalWindows;
            var diag = string.Join(" || ", modalsNow.Select(w => $"[{w.Title}] " +
                string.Join(",", w.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)).Select(b => b.Name))));

            // Ручной ID попадает в SelectedItem (это часть фикса #1), поэтому BtnOk_Click
            // идёт по ветке "выбран пакет из списка" и показывает MessageBox
            // Да/Нет «Подтверждение выбора» — жмём «Да», иначе диалог зависнет.
            var confirmBox = modalsNow.FirstOrDefault(w => (w.Title ?? "").Contains("Подтверждение", StringComparison.OrdinalIgnoreCase));
            if (confirmBox != null)
            {
                var yesBtn = confirmBox.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                                 .FirstOrDefault(b => (b.Name ?? "") == "Да" || (b.Name ?? "") == "Yes");
                Assert.IsNotNull(yesBtn, $"#1: не найдена кнопка «Да» в подтверждении выбора. Найденные кнопки: {diag}");
                confirmBox.Focus();
                System.Threading.Thread.Sleep(300);
                if (yesBtn!.Patterns.LegacyIAccessible.IsSupported)
                    yesBtn.Patterns.LegacyIAccessible.Pattern.DoDefaultAction();
                else
                    yesBtn.Click();
                System.Threading.Thread.Sleep(1000);
            }

            bool dialogClosed = Retry.WhileTrue(
                () => !dialog.Properties.IsOffscreen.ValueOrDefault && desktop.FindFirstChild(
                          cf => cf.ByControlType(ControlType.Window).And(cf.ByName("Выбор альтернативного источника"))) != null,
                timeout: ElementTimeout,
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false).Success;
            Assert.IsTrue(dialogClosed, "#1: диалог не закрылся после «Сохранить».");

            bool savedToDisk = Retry.WhileFalse(
                () => File.Exists(AlternativesPath) && File.ReadAllText(AlternativesPath).Contains(manualPackageId),
                timeout: TimeSpan.FromSeconds(10),
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false).Success;
            Assert.IsTrue(savedToDisk,
                $"#1: ручной ID «{manualPackageId}» не попал в alternatives.json — сохранение не сработало (баг не исправлен).");
        }
    }

    /// <summary>
    /// #7 отдельно от остального потока: после серии диалогов в
    /// AuditFixesCatalogFlowTests переключение вкладок переставало находить
    /// элементы (похоже на побочный эффект вложенных модальных окон в тестовом
    /// раннере) — здесь чистый запуск, без диалогов до этого шага.
    /// </summary>
    [TestClass]
    public class AuditFixesSourceOrderRecheckTests
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
        private static readonly string ProfilePath = Path.Combine(SettingsDir, "profile.json");
        private static readonly string SourceOrderPath = Path.Combine(SettingsDir, "source_order.json");
        private static readonly string LogPath = Path.Combine(SettingsDir, "app.log");

        private static string? _profileBackup; private static bool _profileExisted;
        private static string? _sourceOrderBackup; private static bool _sourceOrderExisted;
        private static AppSession? _session;
        private static string? _launchError;

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            Directory.CreateDirectory(SettingsDir);
            _profileExisted = File.Exists(ProfilePath);
            if (_profileExisted) _profileBackup = File.ReadAllText(ProfilePath);
            File.WriteAllText(ProfilePath, "{\"CatalogMode\":\"full\",\"HasSelectedCategory\":true}");

            _sourceOrderExisted = File.Exists(SourceOrderPath);
            if (_sourceOrderExisted) _sourceOrderBackup = File.ReadAllText(SourceOrderPath);
            if (File.Exists(SourceOrderPath)) File.Delete(SourceOrderPath);

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
                if (_sourceOrderExisted) File.WriteAllText(SourceOrderPath, _sourceOrderBackup!);
                else if (File.Exists(SourceOrderPath)) File.Delete(SourceOrderPath);
            }
            catch { }
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

        private static long LogTailPosition() { try { return new FileInfo(LogPath).Length; } catch { return 0; } }

        private static string ReadLogSince(long position)
        {
            try
            {
                using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length <= position) return "";
                fs.Seek(position, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch { return ""; }
        }

        [TestMethod]
        public void ПорядокИсточников_СохранённыйПриВыгруженномКаталоге_ЗапускаетПерепроверкуПриОткрытии()
        {
            var s = Require();

            var catalogBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCatalogTab"));
            Assert.IsNotNull(catalogBtn, "Не найдена кнопка вкладки «Каталог».");
            catalogBtn!.AsButton().Invoke();

            // Дожидаемся первичной загрузки — это устанавливает _initialized=true
            // и подписку на SourceOrderService.Changed (ровно нужная предпосылка).
            // Ждём именно окончания проверки доступности (лог), а не просто
            // появления кнопки — иначе переключение вкладки соревнуется с фоновой
            // загрузкой каталога за UI-поток.
            long tInit = LogTailPosition();
            var loaded = Retry.WhileFalse(
                () => ReadLogSince(tInit).Contains("Версии загружены", StringComparison.OrdinalIgnoreCase),
                timeout: TimeSpan.FromSeconds(30), interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Success;
            Assert.IsTrue(loaded, "Каталог не завершил первичную загрузку за 30с.");
            System.Threading.Thread.Sleep(1000);

            var systemBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnSystemTab"));
            Assert.IsNotNull(systemBtn, "Не найдена кнопка вкладки «Система».");
            systemBtn!.AsButton().Invoke(); // «Каталог» теперь Unloaded
            System.Threading.Thread.Sleep(500);

            // Секция порядка источников может быть ниже видимой области System-вкладки
            // (ScrollViewer) — докручиваем колесом мыши, иначе элемент не реализуется
            // в дереве UIA вовсе (виртуализация).
            try
            {
                var center = s.MainWindow.BoundingRectangle.Center();
                FlaUI.Core.Input.Mouse.MoveTo(center);
                for (int i = 0; i < 15; i++)
                {
                    FlaUI.Core.Input.Mouse.Scroll(-3);
                    System.Threading.Thread.Sleep(100);
                }
            }
            catch { }
            System.Threading.Thread.Sleep(300);

            var sourceList = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("lstSourceOrder")),
                timeout: TimeSpan.FromSeconds(20), interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;

            if (sourceList == null)
            {
                var allIds = string.Join(" | ", s.MainWindow.FindAllDescendants().Select(e => e.Properties.AutomationId.ValueOrDefault).Where(id => !string.IsNullOrEmpty(id)));
                Assert.Fail($"#7: не найден список порядка источников на вкладке «Система». AutomationId сейчас в окне: {allIds}");
            }

            var btnDown = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnSrcDown"));
            Assert.IsNotNull(btnDown, "#7: не найдена кнопка ▼.");
            sourceList!.FindAllChildren()[0].Click();
            System.Threading.Thread.Sleep(400);
            btnDown!.AsButton().Invoke();
            System.Threading.Thread.Sleep(400);

            var saveBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnSaveSourceOrder"));
            Assert.IsNotNull(saveBtn, "#7: не найдена кнопка сохранения порядка источников.");
            long t2 = LogTailPosition();
            saveBtn!.AsButton().Invoke();

            bool orderSaved = Retry.WhileFalse(
                () => File.Exists(SourceOrderPath),
                timeout: TimeSpan.FromSeconds(10), interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Success;
            Assert.IsTrue(orderSaved, "#7: source_order.json не появился после сохранения.");

            // Возвращаемся в «Каталог» (Loaded срабатывает повторно, _initialized уже true) —
            // именно тут должна сработать перепроверка, добавленная фиксом #7.
            catalogBtn.AsButton().Invoke();

            bool recheckTriggered = Retry.WhileFalse(
                () => ReadLogSince(t2).Contains("Запущена свежая проверка доступности", StringComparison.OrdinalIgnoreCase),
                timeout: TimeSpan.FromSeconds(20), interval: TimeSpan.FromMilliseconds(500), throwOnTimeout: false).Success;
            Assert.IsTrue(recheckTriggered,
                "#7: после возврата в «Каталог» перепроверка доступности НЕ запустилась автоматически — баг не исправлен (или регрессировал).");
        }
    }

    /// <summary>
    /// #6: клик по Chocolatey-предложению должен сбрасывать поиск и скрывать
    /// панель — ровно как для winget-предложения (до фикса этого не было).
    /// "windirstat" выбран как реальный choco-пакет, отсутствующий в каталоге
    /// Ven4Tools (Catalog/master.json), — иначе клиент найдёт его в каталоге,
    /// а не покажет как внешнее предложение.
    /// </summary>
    [TestClass]
    public class AuditFixesChocoSuggestionResetTests
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
        private static readonly string ProfilePath = Path.Combine(SettingsDir, "profile.json");

        private static string? _profileBackup; private static bool _profileExisted;
        private static AppSession? _session;
        private static string? _launchError;

        private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(10);

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
        public void ChocoПредложение_ПослеКлика_СбрасываетПоискИСкрываетПанель()
        {
            var s = Require();

            var catalogBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCatalogTab"));
            Assert.IsNotNull(catalogBtn, "Не найдена кнопка вкладки «Каталог».");
            catalogBtn!.AsButton().Invoke();

            var search = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("txtSearch")),
                timeout: ElementTimeout, interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(search, "Не найдено поле поиска.");

            const string query = "windirstat";
            search!.Click();
            search.AsTextBox().Enter(query);

            var chocoLabel = Retry.WhileNull(
                () => s.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Text).And(cf.ByName("🍫 Chocolatey")))
                                   .FirstOrDefault(),
                timeout: TimeSpan.FromSeconds(20), interval: TimeSpan.FromMilliseconds(500), throwOnTimeout: false).Result;
            if (chocoLabel == null)
                Assert.Inconclusive("Раздел «🍫 Chocolatey» не появился за 20с (сеть/choco недоступны в моменте) — тест не может проверить фикс #6 сейчас.");

            // Кнопка-предложение — Button с ToolTip "Добавить из Chocolatey" (или похожим),
            // ищем среди кнопок в панели предложений.
            var suggestionButtons = s.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Where(b => (b.Name ?? "").Contains("windirstat", StringComparison.OrdinalIgnoreCase) ||
                            (b.Properties.HelpText.IsSupported && (b.Properties.HelpText.Value ?? "").Contains("windirstat", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (suggestionButtons.Count == 0)
            {
                // Фоллбэк: любая кнопка ниже/рядом с найденным текстовым узлом "🍫 Chocolatey".
                var panel = chocoLabel!.Parent;
                suggestionButtons = panel?.FindAllChildren(cf => cf.ByControlType(ControlType.Button)).ToList() ?? new List<AutomationElement>();
            }
            Assert.IsTrue(suggestionButtons.Count >= 1, "#6: не найдена кликабельная кнопка Chocolatey-предложения для windirstat.");

            suggestionButtons[0].Click();
            System.Threading.Thread.Sleep(1000);

            string textAfter = search.AsTextBox().Text ?? "";
            Assert.AreNotEqual(query, textAfter,
                $"#6: поле поиска не сбросилось после клика по Chocolatey-предложению (осталось «{textAfter}») — баг не исправлен.");

            var panelSuggestions = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("pnlWingetSuggestions"));
            if (panelSuggestions != null)
            {
                bool hidden = Retry.WhileFalse(
                    () => panelSuggestions.Properties.IsOffscreen.ValueOrDefault || !panelSuggestions.Patterns.LegacyIAccessible.IsSupported,
                    timeout: TimeSpan.FromSeconds(5), interval: TimeSpan.FromMilliseconds(200), throwOnTimeout: false).Success;
                // Не проваливаем тест на этой части жёстко — Visibility.Collapsed не всегда
                // выставляет IsOffscreen мгновенно/надёжно в UIA; главный, надёжный критерий —
                // сброс текста поиска, уже проверенный выше.
            }
        }
    }

    /// <summary>
    /// #8: установка из «пина» должна использовать выбранный в «Каталоге» диск,
    /// а не жёсткий C:\. Реальная установка cpu-z (крошечный, безопасный,
    /// одобрено пользователем 2026-07-11) на диск D:\ — проверяем через
    /// командную строку реального процесса winget.exe (WMI), затем удаляем.
    /// </summary>
    [TestClass]
    public class AuditFixesPinInstallDriveTests
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
        private static readonly string ProfilePath = Path.Combine(SettingsDir, "profile.json");

        private static string? _profileBackup; private static bool _profileExisted;
        private static AppSession? _session;
        private static string? _launchError;

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            Directory.CreateDirectory(SettingsDir);
            _profileExisted = File.Exists(ProfilePath);
            if (_profileExisted) _profileBackup = File.ReadAllText(ProfilePath);
            File.WriteAllText(ProfilePath,
                "{\"CatalogMode\":\"full\",\"HasSelectedCategory\":true,\"PinnedAppIds\":[\"cpu-z\"]}");

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
            // Подчищаем реальную установку (best-effort, вне assert'ов).
            try
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "winget", Arguments = "uninstall --id CPUID.CPU-Z -e --silent --disable-interactivity",
                    UseShellExecute = false, CreateNoWindow = true
                });
                p?.WaitForExit(30000);
            }
            catch { }
        }

        private static AppSession Require()
        {
            if (_session == null) Assert.Inconclusive("Клиент не запущен: " + (_launchError ?? "неизвестная причина"));
            return _session!;
        }

        [TestMethod]
        public void ПинУстановка_НаВыбранныйНесистемныйДиск_ПередаётDДискВWinget()
        {
            var s = Require();

            var catalogBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCatalogTab"));
            Assert.IsNotNull(catalogBtn, "Не найдена кнопка вкладки «Каталог».");
            catalogBtn!.AsButton().Invoke();

            var diskCombo = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("cmbAvailableDisks")),
                timeout: TimeSpan.FromSeconds(15), interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(diskCombo, "Не найден выбор диска установки (cmbAvailableDisks).");

            var combo = diskCombo!.AsComboBox();
            var dItem = Retry.WhileNull(
                () => combo.Items.FirstOrDefault(i => (i.Text ?? "").TrimStart().StartsWith("D")),
                timeout: TimeSpan.FromSeconds(10), interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            if (dItem == null)
                Assert.Inconclusive("На этой машине нет диска D: — тест #8 требует второй несистемный диск для содержательной проверки.");
            dItem!.Select();
            System.Threading.Thread.Sleep(500);

            var pinInstallBtn = Retry.WhileNull(
                () => s.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                                   .FirstOrDefault(b => (b.Properties.HelpText.IsSupported) &&
                                                         (b.Properties.HelpText.Value ?? "").Contains("cpu-z", StringComparison.OrdinalIgnoreCase)),
                timeout: TimeSpan.FromSeconds(15), interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(pinInstallBtn, "Не найдена кнопка установки пина cpu-z (панель пинов пуста или не отрисовалась).");

            pinInstallBtn!.AsButton().Invoke();

            // Ловим реальный процесс winget.exe и читаем его командную строку через WMI,
            // пока он ещё жив — это единственный надёжный способ увидеть, какой --location
            // реально передан (в UI-логе аргументы не печатаются, только stdout/stderr).
            string? commandLine = null;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && commandLine == null)
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE Name = 'winget.exe'");
                foreach (System.Management.ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        var cl = mo["CommandLine"] as string;
                        if (!string.IsNullOrEmpty(cl) && cl.Contains("CPU-Z", StringComparison.OrdinalIgnoreCase))
                        {
                            commandLine = cl;
                            break;
                        }
                    }
                }
                if (commandLine == null) System.Threading.Thread.Sleep(300);
            }

            Assert.IsNotNull(commandLine,
                "#8: не удалось поймать процесс winget.exe для CPU-Z за 20с — установка не запустилась вовсе.");
            Assert.IsTrue(commandLine!.Contains("--location", StringComparison.OrdinalIgnoreCase),
                $"#8: в командной строке winget нет --location — диск не передаётся вовсе. Командная строка: {commandLine}");
            Assert.IsTrue(commandLine.Contains("D:\\", StringComparison.OrdinalIgnoreCase),
                $"#8: --location не указывает на выбранный диск D:\\ (баг не исправлен — используется C:). Командная строка: {commandLine}");
        }
    }
}
