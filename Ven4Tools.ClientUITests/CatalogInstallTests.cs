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
    /// Регресс-тест реальной установки приложения через прямую ссылку (Direct).
    ///
    /// Форсирует источник "direct" для категории приложения через тот же файл
    /// настроек, который пишет UI (source_order.json), запускает клиент и реально
    /// устанавливает AutoHotkey — единственный надёжный способ проверить, что
    /// Sha256 из каталога доходит до InstallationService (без форсирования Winget
    /// на этой машине перехватил бы установку первым, и Direct-путь не был бы
    /// проверен вовсе).
    ///
    /// Перед запуском бэкапит существующий source_order.json пользователя (если
    /// есть) и восстанавливает его после — реальные локальные настройки машины не
    /// должны пострадать.
    /// </summary>
    [TestClass]
    public class CatalogInstallTests
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");

        private static readonly string SettingsPath = Path.Combine(SettingsDir, "source_order.json");

        private static string? _backupContent;
        private static bool _settingsExistedBefore;
        private static AppSession? _session;
        private static string? _launchError;

        private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(3);

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            _settingsExistedBefore = File.Exists(SettingsPath);
            if (_settingsExistedBefore)
                _backupContent = File.ReadAllText(SettingsPath);

            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath,
                "{\"Mode\":\"per_category\",\"GlobalOrder\":[\"winget\",\"direct\",\"choco\"]," +
                "\"CategoryPrimary\":{\"Другое\":\"direct\"}}");

            try
            {
                _session = AppSession.Launch();
            }
            catch (Exception ex)
            {
                _launchError = ex.Message;
                _session = null;
            }
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            _session?.Dispose();
            _session = null;

            try
            {
                if (_settingsExistedBefore)
                    File.WriteAllText(SettingsPath, _backupContent!);
                else if (File.Exists(SettingsPath))
                    File.Delete(SettingsPath);
            }
            catch { }

            // Тест реально устанавливает AutoHotkey на машину (для проверки настоящего
            // Direct-скачивания с SHA256) — убираем его за собой через winget.
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("winget",
                    "uninstall --id AutoHotkey.AutoHotkey --silent --accept-source-agreements")
                { UseShellExecute = false, CreateNoWindow = true };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(30000);
            }
            catch { /* best-effort уборка, не блокирует результат теста */ }
        }

        private static AppSession Require()
        {
            if (_session == null)
            {
                Assert.Inconclusive(
                    "Клиент Ven4Tools не запущен, тест пропущен. Причина: " +
                    (_launchError ?? "неизвестна") +
                    ". Запустите тест в интерактивной сессии «от имени администратора».");
            }
            return _session!;
        }

        [TestMethod]
        public void Установка_ЧерезПрямуюСсылку_AutoHotkey_БезОшибкиSHA256_ИПереустановкаИзИстории()
        {
            var s = Require();

            // Первый запуск показывает модальный мастер выбора режима каталога
            // (Ven4Tools.Views.CategorySelectionWindow) — это ОТДЕЛЬНОЕ окно (не
            // потомок MainWindow в дереве автоматизации, хотя у обоих одинаковый
            // Title="Ven4Tools"), поэтому ищем через Desktop, а не через MainWindow.
            // Выбираем «Полный», чтобы не потерять категорию «Другое» (там лежит
            // AutoHotkey) — иначе чекбоксы приложений скрыты фильтром режима.
            // Border-карточки (cardBasic/cardExtended/cardFull) не создают собственный
            // элемент в дереве UI Automation — их дочерние TextBlock попадают в дерево
            // напрямую, минуя Border. Поэтому кликаем по тексту заголовка «Полный»:
            // клик идёт по экранным координатам этого текста, что физически попадает
            // в границы окружающего Border и корректно вызывает его MouseDown.
            var desktop = s.Automation.GetDesktop();
            var fullCardTitle = Retry.WhileNull(
                () => desktop.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text)
                                                          .And(cf.ByName("Полный"))),
                timeout: TimeSpan.FromSeconds(15),
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false).Result;

            if (fullCardTitle != null)
            {
                fullCardTitle.Click();

                var continueBtn = Retry.WhileNull(
                    () => desktop.FindFirstDescendant(cf => cf.ByAutomationId("btnContinue")),
                    timeout: TimeSpan.FromSeconds(5),
                    interval: TimeSpan.FromMilliseconds(200),
                    throwOnTimeout: false).Result;
                Assert.IsNotNull(continueBtn, "Не найдена кнопка «Продолжить» в мастере первого запуска.");

                Retry.WhileFalse(
                    () => continueBtn!.AsButton().IsEnabled,
                    timeout: TimeSpan.FromSeconds(5),
                    interval: TimeSpan.FromMilliseconds(200),
                    throwOnTimeout: false);
                Assert.IsTrue(continueBtn!.AsButton().IsEnabled,
                    "Кнопка «Продолжить» не стала активной после выбора карточки «Полный».");

                continueBtn.AsButton().Invoke();
            }

            var catalogBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCatalogTab"));
            Assert.IsNotNull(catalogBtn, "Не найдена кнопка вкладки «Каталог».");
            catalogBtn!.AsButton().Invoke();

            var checkBox = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("chkApp_autohotkey")),
                timeout: TimeSpan.FromSeconds(30),
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false).Result;

            if (checkBox == null)
            {
                var shotPath = Path.Combine(Path.GetTempPath(), "ven4tools_diag_screenshot.png");
                try
                {
                    var img = FlaUI.Core.Capturing.Capture.Element(s.MainWindow);
                    img.ToFile(shotPath);
                }
                catch { shotPath = "(не удалось сделать скриншот)"; }

                var allCheckBoxes = s.MainWindow.FindAllDescendants(cf =>
                    cf.ByControlType(FlaUI.Core.Definitions.ControlType.CheckBox));
                var topLevelNames = s.MainWindow.FindAllChildren()
                    .Select(c => $"{c.ControlType}:'{c.Name}'");
                var diag = allCheckBoxes.Select(cb =>
                    $"AutomationId='{cb.Properties.AutomationId.ValueOrDefault}' Name='{cb.Name}'");
                Assert.Fail("Не найден чекбокс приложения AutoHotkey (chkApp_autohotkey). " +
                    $"Всего чекбоксов в дереве: {allCheckBoxes.Length}. Найдены: " +
                    string.Join(" || ", diag) +
                    $" | Скриншот: {shotPath} | Прямые дети главного окна: " +
                    string.Join(" || ", topLevelNames));
            }

            checkBox!.AsCheckBox().IsChecked = true;

            var installBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnInstall"));
            Assert.IsNotNull(installBtn, "Не найдена кнопка «Установить выбранные».");

            Retry.WhileFalse(
                () => installBtn!.AsButton().IsEnabled,
                timeout: TimeSpan.FromSeconds(10),
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false);
            Assert.IsTrue(installBtn!.AsButton().IsEnabled,
                "Кнопка «Установить выбранные» не стала активной после выбора AutoHotkey " +
                "(возможно, проверка доступности источников ещё не завершилась).");

            installBtn.AsButton().Invoke();

            var overallStatus = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("txtOverallStatus")),
                timeout: ElementTimeout,
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false).Result;
            Assert.IsNotNull(overallStatus, "Не найден текст общего статуса установки (txtOverallStatus).");

            var finished = Retry.WhileFalse(
                () =>
                {
                    var text = overallStatus!.Name ?? string.Empty;
                    return text.Contains("завершена") || text.Contains("отменена") || text.Contains("прервана");
                },
                timeout: InstallTimeout,
                interval: TimeSpan.FromSeconds(1),
                throwOnTimeout: false).Success;

            string finalStatusText = overallStatus!.Name ?? string.Empty;
            Assert.IsTrue(finished,
                $"Установка не завершилась за {InstallTimeout.TotalMinutes} мин. Последний статус: '{finalStatusText}'.");

            var progressListBox = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("lstAppProgress"));
            Assert.IsNotNull(progressListBox, "Не найден список прогресса установки (lstAppProgress).");

            var descendantTexts = progressListBox!.FindAllDescendants()
                .Select(el => el.Name ?? string.Empty)
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();

            bool hasNoSha256Error = descendantTexts.Any(t => t.Contains("Нет SHA256"));
            Assert.IsFalse(hasNoSha256Error,
                "Обнаружена ошибка «Нет SHA256 в каталоге» при установке через прямую ссылку — " +
                "пробрасывание Sha256 из каталога в AppInfo снова не работает. " +
                $"Итоговый статус: '{finalStatusText}'. Найденные строки прогресса: " +
                string.Join(" | ", descendantTexts));

            Assert.IsTrue(finalStatusText.Contains("Успешно: 1"),
                $"Установка AutoHotkey не завершилась успехом. Итоговый статус: '{finalStatusText}'. " +
                "Найденные строки прогресса: " + string.Join(" | ", descendantTexts));

            // ── Сценарий F: переустановка того же приложения из вкладки «История» ──
            // Обратная связь по переустановке идёт только через AppLogger.Write, который
            // MainWindow подписывает на глобальный лог (lstGlobalLog) — там и проверяем
            // итоговый текст, отдельного прогресс-бара на этой вкладке нет.
            var historyBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnHistoryTab"));
            Assert.IsNotNull(historyBtn, "Не найдена кнопка вкладки «История».");
            historyBtn!.AsButton().Invoke();

            var historyList = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("lstHistory")),
                timeout: ElementTimeout,
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false).Result;
            Assert.IsNotNull(historyList, "Не найден список истории установок (lstHistory).");

            var reinstallBtn = Retry.WhileNull(
                () => historyList!.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button)
                                                              .And(cf.ByName("🔄")))
                                   .FirstOrDefault(),
                timeout: ElementTimeout,
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false).Result;
            Assert.IsNotNull(reinstallBtn,
                "Не найдена кнопка «Переустановить» (🔄) в истории — запись об установке AutoHotkey не появилась.");

            var globalLog = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("lstGlobalLog"));
            Assert.IsNotNull(globalLog, "Не найден глобальный лог (lstGlobalLog).");

            // ListBoxItem.Name по умолчанию — это ToString() привязанного LogEntry,
            // а не отображаемый текст; реальный текст лежит в дочернем TextBlock.
            static List<string> LogTexts(AutomationElement log) =>
                log.FindAllDescendants()
                   .Select(el => el.Name ?? string.Empty)
                   .Where(t => !string.IsNullOrEmpty(t) && t != "Ven4Tools.Views.Tabs.LogEntry")
                   .ToList();

            reinstallBtn!.AsButton().Invoke();

            var reinstallDone = Retry.WhileFalse(
                () => LogTexts(globalLog!).Any(t => t.Contains("переустановлен") || t.Contains("❌")),
                timeout: InstallTimeout,
                interval: TimeSpan.FromSeconds(1),
                throwOnTimeout: false).Success;

            var logTexts = LogTexts(globalLog!);
            Assert.IsTrue(reinstallDone,
                "Переустановка из истории не завершилась (ни успеха, ни ошибки в логе за отведённое время). " +
                "Последние строки лога: " + string.Join(" | ", logTexts.TakeLast(10)));

            bool reinstallHasNoSha256Error = logTexts.Any(t => t.Contains("Нет SHA256") || t.Contains("не указан SHA256"));
            Assert.IsFalse(reinstallHasNoSha256Error,
                "При переустановке из истории снова возникла ошибка отсутствия SHA256. " +
                "Строки лога: " + string.Join(" | ", logTexts.TakeLast(10)));

            bool reinstallSucceeded = logTexts.Any(t => t.Contains("переустановлен"));
            Assert.IsTrue(reinstallSucceeded,
                "Переустановка AutoHotkey из истории не подтвердилась в логе как успешная. " +
                "Строки лога: " + string.Join(" | ", logTexts.TakeLast(10)));
        }
    }
}
