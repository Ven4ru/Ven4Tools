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
        public void СистемнаяИнформация_КопированиеВБуфер_РаботаетИМеняетСодержимое()
        {
            var s = Require();
            var systemBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnSystemTab"));
            Assert.IsNotNull(systemBtn, "Не найдена кнопка вкладки «Система».");
            systemBtn!.AsButton().Invoke();

            // SystemTab — вложенный TabControl из 5 под-вкладок; контент не выбранной
            // под-вкладки не реализуется в дереве UIA. btnCopySystemInfo — в «Диагностика».
            var diagSubTab = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.TabItem).And(cf.ByName("Диагностика"))),
                timeout: T, interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(diagSubTab, "Не найдена под-вкладка «Диагностика» на вкладке «Система».");
            diagSubTab!.Click();
            System.Threading.Thread.Sleep(300);

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
                timeout: T, interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(copyBtn, "Не найдена кнопка «Копировать информацию».");
            copyBtn!.AsButton().Invoke();
            System.Threading.Thread.Sleep(500);

            string clipboardText = ReadPwsh("Get-Clipboard -Raw");

            Assert.IsFalse(string.IsNullOrEmpty(clipboardText) || clipboardText == sentinel,
                "Кнопка «Копировать информацию» не изменила содержимое буфера обмена.");
        }

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

            runAllBtn!.AsButton().Invoke();

            // Диагностика включает сетевые запросы (пинг/DNS/публичный IP) — даём до 60с.
            bool reEnabled = Retry.WhileFalse(
                () => runAllBtn.AsButton().IsEnabled,
                timeout: TimeSpan.FromSeconds(60), interval: TimeSpan.FromMilliseconds(500), throwOnTimeout: false).Success;
            Assert.IsTrue(reEnabled, "Кнопка полной диагностики не вернулась в активное состояние за 60с — возможно зависание.");
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
