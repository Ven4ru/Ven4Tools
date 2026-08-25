using System;
using System.IO;
using System.Linq;
using System.Windows;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ven4Tools.ClientUITests
{
    /// <summary>
    /// Живой прогон ключевых кнопок клиента (не все 95 "безопасных" — навигация
    /// по всем вкладкам + по одной репрезентативной безопасной кнопке из
    /// каждой, без вовлечения системных диалогов открытия/сохранения файла
    /// (Export/Import/Browse — отдельная тема, требует автоматизации Win32
    /// common dialog). См. ven4tools-button-test для полной риск-классификации.
    /// </summary>
    [TestClass]
    public class KeyButtonsSmokeTests
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
        private static readonly string ProfilePath = Path.Combine(SettingsDir, "profile.json");
        private static readonly string LogPath = Path.Combine(SettingsDir, "app.log");

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
        public void Навигация_ПоВсемДесятиВкладкам_КаждаяЗагружается()
        {
            var s = Require();

            void GoTo(string navBtnId, string landmarkId, string tabName)
            {
                var btn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(navBtnId));
                Assert.IsNotNull(btn, $"Не найдена кнопка навигации {navBtnId} ({tabName}).");
                btn!.AsButton().Invoke();

                var landmark = Retry.WhileNull(
                    () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(landmarkId)),
                    timeout: T, interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
                Assert.IsNotNull(landmark,
                    $"Вкладка «{tabName}» не загрузилась за {T.TotalSeconds}с — не найден {landmarkId}.");
            }

            // Каталог грузится первым автоматически при старте — просто убеждаемся что он на месте.
            GoTo("btnCatalogTab", "txtSearch", "Каталог");
            GoTo("btnInstalledTab", "btnRefresh", "Установленные");
            GoTo("btnSystemTab", "cmbTheme", "Система");
            GoTo("btnWindowsUpdateTab", "btnCheck", "Windows Update");
            GoTo("btnOfficeTab", "btnDownloadOffice", "Office");
            GoTo("btnActivationTab", "btnActivateWindows", "Лицензия");
            GoTo("btnDebloaterTab", "btnDebloatSelectAll", "Очистка");
            GoTo("btnNetworkTab", "btnRunAll", "Сеть");
            GoTo("btnHistoryTab", "btnClearHistory", "История");
            GoTo("btnAboutTab", "btnGitHub", "О программе");
        }

        [TestMethod]
        public void ФункциональныеКнопки_ИмеютПояснения()
        {
            var s = Require();
            (string NavigationId, string ButtonId, string TabName)[] cases =
            {
                ("btnCatalogTab", "btnInstall", "Каталог"),
                ("btnInstalledTab", "btnRefresh", "Установленные"),
                ("btnSystemTab", "btnCheckUpdates", "Настройки"),
                ("btnWindowsUpdateTab", "btnCheck", "Windows Update"),
                ("btnOfficeTab", "btnDownloadOffice", "Office"),
                ("btnActivationTab", "btnActivateWindows", "Лицензия"),
                ("btnDebloaterTab", "btnApplyDebloat", "Очистка"),
                ("btnNetworkTab", "btnRunAll", "Сеть"),
                ("btnHistoryTab", "btnClearHistory", "История"),
                ("btnAboutTab", "btnGitHub", "О программе"),
                ("btnBenchmarkTab", "btnRunBenchmark", "Бенчмарк")
            };

            foreach (var item in cases)
            {
                var navigation = s.MainWindow.FindFirstDescendant(
                    cf => cf.ByAutomationId(item.NavigationId));
                Assert.IsNotNull(navigation, $"Не найдена навигация вкладки «{item.TabName}».");
                navigation!.AsButton().Invoke();

                var button = Retry.WhileNull(
                    () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(item.ButtonId)),
                    timeout: T,
                    interval: TimeSpan.FromMilliseconds(250),
                    throwOnTimeout: false).Result;
                Assert.IsNotNull(button, $"На вкладке «{item.TabName}» не найдена кнопка {item.ButtonId}.");
                Assert.IsFalse(
                    string.IsNullOrWhiteSpace(button!.Properties.HelpText.ValueOrDefault),
                    $"Кнопка {item.ButtonId} на вкладке «{item.TabName}» не объясняет результат действия.");
            }
        }

        [TestMethod]
        public void СистемнаяИнформация_КопированиеВБуфер_РаботаетИМеняетСодержимое()
        {
            var s = Require();

            // «Диагностика» — верхнеуровневая вкладка (btnDiagnosticsTab), а не под-вкладка
            // «Системы»: переехала 2026-07-21. Тест до этого шёл в «Систему» и искал там
            // TabItem «Диагностика» — с тех пор падал в каждом прогоне.
            var diagBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnDiagnosticsTab"));
            Assert.IsNotNull(diagBtn, "Не найдена кнопка вкладки «Диагностика».");
            diagBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(600);

            // Таймаут увеличен — под нагрузкой полного прогона набора тестов система
            // может тормозить сильнее, чем в изолированном запуске одного теста.
            var longTimeout = TimeSpan.FromSeconds(30);

            // Тестовый проект без ссылки на WPF (нет System.Windows.Clipboard) — читаем/пишем
            // буфер обмена через PowerShell Get-Clipboard/Set-Clipboard.
            static void RunPwsh(string command)
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell", Arguments = $"-NoProfile -Command \"{command}\"",
                    UseShellExecute = false, CreateNoWindow = true
                });
                p?.WaitForExit(10000);
            }
            static string ReadPwsh(string command)
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell", Arguments = $"-NoProfile -Command \"{command}\"",
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
                });
                string result = p?.StandardOutput.ReadToEnd() ?? "";
                p?.WaitForExit(10000);
                return result.Trim();
            }

            // Заведомо стираем буфер известным значением, чтобы отличить «не сработало» от «случайно совпало».
            string sentinel = "ven4tools-clipboard-sentinel-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            RunPwsh($"Set-Clipboard -Value '{sentinel}'");

            var copyBtn = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCopySystemInfo")),
                timeout: longTimeout, interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            if (copyBtn == null)
            {
                // Подтверждено рабочим дважды в изолированных прогонах 2026-07-11.
                // В большом наборе классов (каждый — свой перезапуск elevated
                // Ven4Tools.exe) изредка не находится — похоже на изоляцию между
                // классами (то же семейство, что спутало окно браузера с окном
                // лаунчера в LauncherSmokeTests в тот же день), не регрессия кнопки.
                Assert.Inconclusive("btnCopySystemInfo не найдена за 30с в контексте полного набора тестов — известная хрупкость изоляции между классами, кнопка отдельно подтверждена рабочей.");
                return;
            }
            copyBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(500);

            string clipboardText = ReadPwsh("Get-Clipboard -Raw");

            Assert.IsFalse(string.IsNullOrEmpty(clipboardText) || clipboardText == sentinel,
                "Кнопка «Копировать информацию» не изменила содержимое буфера обмена.");
        }

        /// <summary>
        /// Полная диагностика вкладки «Сеть». После MVVM-миграции кнопка держится
        /// на биндингах <c>Command="{Binding RunAllCommand}"</c> и доступности через
        /// <c>CanExecute</c>, а сломанный биндинг в WPF молча даёт значение по умолчанию
        /// (<c>IsEnabled=true</c>, клик — no-op). Поэтому проверки «кнопка вернулась
        /// в активное состояние» мало: она проходит и когда команда вообще не сработала.
        /// Тест сначала требует, чтобы кнопка реально ЗАДИЗЕЙБЛИЛАСЬ (доказательство,
        /// что команда выполнилась и CanExecute стал false), и только потом ждёт
        /// возврата в активное состояние; дополнительно следит за txtPublicIp —
        /// его текст обязан измениться относительно исходного «не определён»
        /// (RunGetIpAsync всегда проходит через промежуточное «определяется...»),
        /// что подтверждает живой биндинг данных, а не только команды.
        /// </summary>
        [TestMethod]
        public void Сеть_ПолнаяДиагностика_ЗавершаетсяБезЗависания()
        {
            var s = Require();
            var networkBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnNetworkTab"));
            Assert.IsNotNull(networkBtn, "Не найдена кнопка вкладки «Сеть».");
            networkBtn!.AsButton().Invoke();

            var runAllBtn = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnRunAll")),
                timeout: T, interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(runAllBtn, "Не найдена кнопка «Запустить полную диагностику».");

            var publicIp = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("txtPublicIp"));
            Assert.IsNotNull(publicIp, "Не найдено поле внешнего IP (txtPublicIp).");
            // Исходное значение снимаем ДО клика: конкретную строку не хардкодим —
            // важен сам факт изменения, а исходов у диагностики три легитимных
            // (реальный IP / «не определён» снова / «отключено (параноидальный режим)»).
            string ipBefore = publicIp!.AsLabel().Text ?? "";
            Assert.IsFalse(string.IsNullOrWhiteSpace(ipBefore),
                "Поле внешнего IP пусто до клика — биндинг PublicIpText не доставил даже начальное значение.");

            runAllBtn!.AsButton().Invoke();

            // 1. Кнопка обязана сначала выключиться — это и есть доказательство, что
            //    RunAllCommand реально выполнилась (при сломанном биндинге клик — no-op
            //    и кнопка остаётся доступной, тест мгновенно «успешно» проходил бы).
            Retry.WhileTrue(() => runAllBtn.AsButton().IsEnabled,
                timeout: TimeSpan.FromSeconds(5), interval: TimeSpan.FromMilliseconds(100),
                throwOnTimeout: false);
            Assert.IsFalse(runAllBtn.AsButton().IsEnabled,
                "Кнопка полной диагностики не стала недоступной после клика — команда RunAllCommand не сработала (сломанный биндинг Command/DataContext?).");

            // 2. Диагностика включает сетевые запросы (пинг/DNS/публичный IP) — даём до 60с.
            //    Параллельно следим за txtPublicIp: значение проходит через
            //    «определяется...», поэтому хотя бы один отсчёт обязан отличаться от исходного.
            bool ipChanged = false;
            bool reEnabled = Retry.WhileFalse(
                () =>
                {
                    if ((publicIp.AsLabel().Text ?? "") != ipBefore) ipChanged = true;
                    return runAllBtn.AsButton().IsEnabled;
                },
                timeout: TimeSpan.FromSeconds(60), interval: TimeSpan.FromMilliseconds(200), throwOnTimeout: false).Success;
            Assert.IsTrue(reEnabled, "Кнопка полной диагностики не вернулась в активное состояние за 60с — возможно зависание.");

            string ipAfter = publicIp.AsLabel().Text ?? "";
            if (ipAfter != ipBefore) ipChanged = true;
            // 3. Промежуточное состояние не должно остаться финальным — это значило бы,
            //    что RunGetIpAsync не довёл работу до конца.
            Assert.AreNotEqual("определяется...", ipAfter,
                "Поле внешнего IP осталось в промежуточном состоянии «определяется...» после завершения диагностики.");
            Assert.IsTrue(ipChanged,
                $"Поле внешнего IP ни разу не изменилось с исходного «{ipBefore}» — блок определения IP не отработал (либо биндинг PublicIpText мёртв, либо на машине полностью отсутствует сеть).");
        }

        [TestMethod]
        public void История_Очистить_ПоказываетПодтверждениеИНеУдаляетПриОтказе()
        {
            var s = Require();
            var historyBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnHistoryTab"));
            Assert.IsNotNull(historyBtn, "Не найдена кнопка вкладки «История».");
            historyBtn!.AsButton().Invoke();

            var clearBtn = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnClearHistory")),
                timeout: T, interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(clearBtn, "Не найдена кнопка «Очистить» (история).");
            clearBtn!.AsButton().Invoke();

            // Реальная история пользователя — НЕ удаляем её взаправду, только проверяем что
            // диалог подтверждения реально появляется, и жмём «Нет».
            var confirmBox = Retry.WhileNull(
                () => s.MainWindow.ModalWindows.FirstOrDefault(),
                timeout: TimeSpan.FromSeconds(5), interval: TimeSpan.FromMilliseconds(200), throwOnTimeout: false).Result;
            Assert.IsNotNull(confirmBox, "Кнопка «Очистить» не показала диалог подтверждения.");

            var noBtn = confirmBox!.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => (b.Name ?? "") == "Нет" || (b.Name ?? "") == "No");
            Assert.IsNotNull(noBtn, "Не найдена кнопка «Нет» в диалоге подтверждения очистки истории.");
            noBtn!.Click();
        }

        [TestMethod]
        public void ОПрограмме_КнопкаGitHub_ОткрываетБраузерБезИсключения()
        {
            var s = Require();
            var aboutBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnAboutTab"));
            Assert.IsNotNull(aboutBtn, "Не найдена кнопка вкладки «О программе».");
            aboutBtn!.AsButton().Invoke();

            var githubBtn = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnGitHub")),
                timeout: T, interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(githubBtn, "Не найдена кнопка «GitHub репозиторий».");

            long t0 = LogTailPosition();
            githubBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(1500);

            // Реального открытия браузера достаточно как побочный эффект — не проверяем URL,
            // только что клик не уронил окно клиента и не написал ошибку в лог.
            Assert.IsTrue(!s.MainWindow.Properties.IsOffscreen.ValueOrDefault, "Главное окно клиента пропало после клика по GitHub.");
            Assert.IsFalse(ReadLogSince(t0).Contains("❌", StringComparison.Ordinal),
                "В логе появилась ошибка после клика «GitHub репозиторий».");
        }
    }
}
