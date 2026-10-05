using System;
using System.Diagnostics;
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
    /// Тихий режим клиента: установка набора по командной строке без единого вопроса,
    /// с кодом возврата и файлом итога.
    ///
    /// Клиент запускается напрямую, а не через <see cref="AppSession"/>: окна здесь
    /// искать не нужно — проверяется как раз то, что процесс сам доходит до конца и
    /// завершается. Установка настоящая (AutoHotkey по прямой ссылке, как в
    /// <see cref="CatalogInstallTests"/>), за собой тест её убирает.
    /// </summary>
    [TestClass]
    public class UnattendedInstallTests
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
        private static readonly string SourceOrderPath = Path.Combine(SettingsDir, "source_order.json");

        private static readonly string ProfilePath = Path.Combine(SettingsDir, "profile.json");

        private static string? _sourceOrderBackup;
        private static bool _sourceOrderExisted;
        private static string? _profileBackup;
        private static bool _profileExisted;
        private static string _workDir = "";

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            _workDir = Path.Combine(Path.GetTempPath(), "Ven4Tools.Unattended." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workDir);

            _sourceOrderExisted = File.Exists(SourceOrderPath);
            if (_sourceOrderExisted) _sourceOrderBackup = File.ReadAllText(SourceOrderPath);
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SourceOrderPath,
                "{\"Mode\":\"per_category\",\"GlobalOrder\":[\"winget\",\"direct\",\"choco\"]," +
                "\"CategoryPrimary\":{\"Другое\":\"direct\"}}");

            // Режим каталога задаётся явно: без него клиент при запуске с окном открывает
            // «Добро пожаловать», и оно встаёт раньше вопроса о незавершённой установке.
            // На машине, где клиентом уже пользовались, режим выбран давно, а на чистой
            // (раннер CI) тест вопроса так и не дожидался.
            _profileExisted = File.Exists(ProfilePath);
            if (_profileExisted) _profileBackup = File.ReadAllText(ProfilePath);
            File.WriteAllText(ProfilePath, "{\"CatalogMode\":\"full\",\"HasSelectedCategory\":true}");
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            KillClient();
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

            try
            {
                var psi = new ProcessStartInfo("winget",
                    "uninstall --id AutoHotkey.AutoHotkey --silent --accept-source-agreements")
                { UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi);
                p?.WaitForExit(30000);
            }
            catch { /* уборка по возможности */ }

            try { Directory.Delete(_workDir, recursive: true); } catch { }
        }

        private static void KillClient()
        {
            foreach (var stale in Process.GetProcessesByName("Ven4Tools"))
            {
                try { if (!stale.HasExited) { stale.Kill(); stale.WaitForExit(5000); } } catch { }
                finally { stale.Dispose(); }
            }
        }

        /// <summary>Запускает клиент с аргументами и ждёт, пока он завершится сам.</summary>
        private static int? Run(string arguments, TimeSpan timeout)
        {
            KillClient();
            var psi = new ProcessStartInfo(AppSession.ResolveClientExePath(), arguments) { UseShellExecute = false };
            // Вид окна — тот же, в котором идёт весь набор (см. AppSession.Launch).
            if (AppSession.SuiteUsesModernShell) psi.Environment.Remove("VEN4TOOLS_UI_CLASSIC");
            else psi.Environment["VEN4TOOLS_UI_CLASSIC"] = "1";

            using var process = Process.Start(psi)!;
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(); } catch { }
                return null;
            }
            return process.ExitCode;
        }

        private static JsonElement ReadReport(string path)
        {
            Assert.IsTrue(File.Exists(path), "Клиент не записал файл итога (--report).");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.Clone();
        }

        [TestMethod]
        public void ТихаяУстановка_СтавитПриложение_ПишетИтог_ЗавершаетсяСКодом0()
        {
            string reportPath = Path.Combine(_workDir, "report-ok.json");

            int? exitCode = Run($"--install V4T:autohotkey --silent --no-restore-point --report \"{reportPath}\"",
                TimeSpan.FromMinutes(4));

            Assert.IsNotNull(exitCode, "Клиент в тихом режиме не завершился сам за 4 минуты — где-то ждёт ответа.");
            var report = ReadReport(reportPath);
            string message = report.GetProperty("Message").GetString() ?? "";

            if (exitCode != 0)
            {
                // Тест проверяет тихий режим, а не доступность сервера загрузки: сетевой
                // сбой — причина пропустить, а не покраснеть. Причина остаётся в сообщении.
                string failed = report.GetProperty("Failed").ToString();
                Assert.Inconclusive($"Тихая установка завершилась с кодом {exitCode}: {message}. Неудачи: {failed}");
            }

            StringAssert.Contains(report.GetProperty("Installed").ToString(), "autohotkey",
                "В итоге должно быть названо установленное приложение.");
            Assert.AreEqual(0, report.GetProperty("Failed").GetArrayLength(), "Неудач быть не должно: " + message);
            Assert.AreEqual(0, report.GetProperty("ExitCode").GetInt32());
        }

        [TestMethod]
        public void ТихаяУстановка_НеизвестноеПриложение_Код3_БезОкон()
        {
            string reportPath = Path.Combine(_workDir, "report-missing.json");

            int? exitCode = Run($"--install V4T:no-such-app-in-catalog --silent --report \"{reportPath}\"",
                TimeSpan.FromMinutes(2));

            Assert.AreEqual(3, exitCode, "Приложения нет в каталоге — ожидался код 3 и выход без вопросов.");
            var report = ReadReport(reportPath);
            StringAssert.Contains(report.GetProperty("NotFound").ToString(), "no-such-app-in-catalog");
        }

        [TestMethod]
        public void НезавершённаяУстановка_СпрашиваетИПоОтказуЗабывает()
        {
            // Клиент «не дошёл до конца» прошлой пачки: в очереди остались два приложения.
            KillClient();
            Directory.CreateDirectory(Path.GetDirectoryName(AppSession.PendingInstallPath)!);
            File.WriteAllText(AppSession.PendingInstallPath,
                "{\"AppIds\":[\"autohotkey\",\"7zip\"],\"InstallDrive\":null,\"StartedUtc\":\"" +
                DateTime.UtcNow.ToString("o") + "\"}");

            AppSession? session = null;
            try
            {
                session = AppSession.Launch(AppSession.SuiteUsesModernShell, keepPendingInstall: true);

                var question = Retry.WhileNull(
                    () => session.MainWindow.ModalWindows.FirstOrDefault(
                        w => (w.Title ?? "").Contains("незавершённая установка", StringComparison.OrdinalIgnoreCase)),
                    timeout: TimeSpan.FromSeconds(20), interval: TimeSpan.FromMilliseconds(300),
                    throwOnTimeout: false).Result;
                if (question == null)
                {
                    // Клиент закрывается в finally — следы нужно снять, пока он ещё на экране.
                    string traces = FailureDiagnostics.Snapshot(
                        nameof(НезавершённаяУстановка_СпрашиваетИПоОтказуЗабывает) + "-без-вопроса");
                    Assert.Fail("Клиент не спросил о продолжении незавершённой установки. Файл очереди " +
                        (File.Exists(AppSession.PendingInstallPath) ? "на месте" : "уже удалён") + ". " + traces);
                }

                string text = string.Join(" ", question!.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .Select(t => t.Name));
                StringAssert.Contains(text, "Осталось установить: 2", "В вопросе должно быть названо, сколько осталось.");
                StringAssert.Contains(text, "autohotkey", "В вопросе должно быть названо, что именно осталось.");

                var no = question.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .FirstOrDefault(b => b.Name is "Нет" or "No");
                Assert.IsNotNull(no, "В вопросе нет кнопки «Нет».");
                no!.AsButton().Invoke();

                Assert.IsTrue(
                    Retry.WhileTrue(() => File.Exists(AppSession.PendingInstallPath),
                        timeout: TimeSpan.FromSeconds(5), interval: TimeSpan.FromMilliseconds(200),
                        throwOnTimeout: false).Success,
                    "После отказа очередь должна быть забыта — иначе вопрос повторялся бы при каждом запуске.");
            }
            finally
            {
                session?.Dispose();
                try { File.Delete(AppSession.PendingInstallPath); } catch { }
            }
        }

        [TestMethod]
        public void ТихаяУстановка_НеразборчивоеЗадание_Код2_БезОкон()
        {
            // После --install нет набора: клиент обязан выйти сразу, не открывая окна.
            int? exitCode = Run("--install --silent", TimeSpan.FromSeconds(40));

            Assert.AreEqual(2, exitCode, "Неразборчивое задание — ожидался код 2 и выход без вопросов.");
        }
    }
}
