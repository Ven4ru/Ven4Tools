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
    /// Окно «Перед переустановкой Windows»: набор собирается в выбранной папке и
    /// содержит всё, что нужно для возврата на свежей системе.
    ///
    /// Драйверы в тесте не выгружаются: это гигабайты и несколько минут, а путь
    /// выгрузки покрыт юнит-тестами. Профили Wi-Fi и список программ собираются
    /// по-настоящему. Систему сборка набора не меняет.
    /// </summary>
    [TestClass]
    public class ReinstallKitUiTests
    {
        // Набор кладётся на несистемный диск, если он есть: только там можно проверить
        // запись файла ответов Windows — в корень системного диска клиент его не пишет.
        private static readonly string? DataDriveRoot = FindDataDrive();
        private static readonly string KitFolder = Path.Combine(
            DataDriveRoot ?? Path.GetTempPath(), "v4t-kit-ui-" + Guid.NewGuid().ToString("N"));
        private static string? AnswerFilePath => DataDriveRoot == null ? null : Path.Combine(DataDriveRoot, "autounattend.xml");
        private static bool _answerFileExisted;

        private static string? FindDataDrive()
        {
            try
            {
                string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                return DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Fixed && d.IsReady
                                && !string.Equals(d.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase)
                                && d.AvailableFreeSpace > 2L * 1024 * 1024 * 1024)
                    .Select(d => d.RootDirectory.FullName)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        private static AppSession? _session;
        private static string? _launchError;

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            _answerFileExisted = AnswerFilePath != null && File.Exists(AnswerFilePath);
            try { _session = AppSession.Launch(); }
            catch (Exception ex) { _launchError = ex.Message; _session = null; }
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            _session?.Dispose();
            _session = null;
            try { if (Directory.Exists(KitFolder)) Directory.Delete(KitFolder, recursive: true); }
            catch { /* уборка — по возможности */ }
            try
            {
                // Файл ответов в корне диска: вернуть прежний, если он был, иначе убрать свой.
                if (AnswerFilePath != null)
                {
                    string backup = AnswerFilePath + ".bak";
                    if (_answerFileExisted && File.Exists(backup)) File.Copy(backup, AnswerFilePath, overwrite: true);
                    else if (!_answerFileExisted && File.Exists(AnswerFilePath)) File.Delete(AnswerFilePath);
                    if (File.Exists(backup)) File.Delete(backup);
                }
            }
            catch { /* уборка — по возможности */ }
        }

        [TestMethod]
        public void НаборСобирается_ИСодержитФайлЗапускаКлиентИСписокПрограмм()
        {
            if (_session == null)
                Assert.Inconclusive("Клиент Ven4Tools не запущен: " + (_launchError ?? "причина неизвестна"));
            var s = _session!;

            UiNav.Find(s, "btnSystemTab")!.AsButton().Invoke();
            var subTab = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.TabItem).And(cf.ByName("Офлайн и приватность"))),
                timeout: TimeSpan.FromSeconds(10), interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(subTab, "Не найдена под-вкладка «Офлайн и приватность».");
            subTab!.Click();

            var open = Retry.WhileNull(() => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnReinstallKit")),
                timeout: TimeSpan.FromSeconds(10), interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(open, "Не найдена кнопка «Перед переустановкой Windows…».");
            open!.AsButton().Invoke();

            var window = Retry.WhileNull(() => s.MainWindow.ModalWindows.FirstOrDefault(),
                timeout: TimeSpan.FromSeconds(10), interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(window, "Окно «Перед переустановкой Windows» не открылось.");

            AutomationElement Get(string id)
            {
                var element = Retry.WhileNull(() => window!.FindFirstDescendant(cf => cf.ByAutomationId(id)),
                    timeout: TimeSpan.FromSeconds(5), interval: TimeSpan.FromMilliseconds(200), throwOnTimeout: false).Result;
                Assert.IsNotNull(element, $"В окне нет элемента {id}.");
                return element!;
            }

            try
            {
                Get("txtKitFolder").AsTextBox().Text = KitFolder;

                var drivers = Get("chkKitDrivers").AsCheckBox();
                if (drivers.IsChecked == true) drivers.Toggle();
                Assert.AreEqual(false, drivers.IsChecked, "Не удалось снять отметку «Драйверы».");
                Assert.AreEqual(true, Get("chkKitWifi").AsCheckBox().IsChecked, "«Профили Wi-Fi» должны быть отмечены по умолчанию.");
                Assert.AreEqual(true, Get("chkKitApps").AsCheckBox().IsChecked, "«Список программ» должен быть отмечен по умолчанию.");
                Assert.IsFalse(Get("btnOpenKitFolder").IsEnabled, "«Открыть папку» доступна до сборки набора.");

                var answerFile = Get("chkKitAnswerFile").AsCheckBox();
                Assert.AreEqual(false, answerFile.IsChecked, "Файл ответов Windows не должен быть отмечен по умолчанию.");
                answerFile.Toggle();
                Get("txtKitAccount").AsTextBox().Text = "Тестовый";

                Get("btnBuildKit").AsButton().Invoke();

                // Копия клиента — сотни мегабайт, плюс два обращения к winget.
                string status = "";
                bool finished = Retry.WhileFalse(() =>
                {
                    status = Get("txtKitStatus").Name;
                    return status.Contains("Набор собран") || status.Contains("не собран");
                }, timeout: TimeSpan.FromMinutes(5), interval: TimeSpan.FromSeconds(1), throwOnTimeout: false).Success;
                Assert.IsTrue(finished, "Сборка набора не завершилась за пять минут. Последнее состояние: " + status);
                StringAssert.Contains(status, "Набор собран", "Набор не собран: " + status);
                StringAssert.Contains(status, "Программы:", "В итоге не названы программы.");
                Assert.IsTrue(Get("btnOpenKitFolder").IsEnabled, "«Открыть папку» недоступна после сборки набора.");

                foreach (string file in new[]
                         {
                             "restore.cmd", "ven4tools-restore.json", "ПРОЧТИ.txt", "Поставить вручную.txt",
                             Path.Combine("Ven4Tools", "Ven4Tools.exe")
                         })
                    Assert.IsTrue(File.Exists(Path.Combine(KitFolder, file)), $"В наборе нет файла {file}.");

                Assert.IsFalse(Directory.Exists(Path.Combine(KitFolder, "Drivers")),
                    "Драйверы выгружены, хотя отметка была снята.");
                StringAssert.Contains(File.ReadAllText(Path.Combine(KitFolder, "ven4tools-restore.json")), "\"apps\"",
                    "В файле ответа нет списка программ.");

                if (AnswerFilePath != null)
                {
                    Assert.IsTrue(File.Exists(AnswerFilePath), "Файл ответов Windows не записан в корень диска набора: " + status);
                    string unattend = File.ReadAllText(AnswerFilePath);
                    StringAssert.Contains(unattend, "<Name>Тестовый</Name>", "В файле ответов нет заданной учётной записи.");
                    StringAssert.Contains(unattend, Path.GetFileName(KitFolder) + "\\restore.cmd",
                        "Команда первого входа не ведёт в папку набора.");
                    Assert.IsFalse(unattend.Contains("windowsPE"), "В файле ответов не должно быть прохода windowsPE.");
                }
                else
                {
                    // Несистемного диска нет: клиент обязан отказаться писать файл в корень системного.
                    StringAssert.Contains(status, "Файл ответов не записан", "Ожидался отказ писать файл ответов на системный диск.");
                }
            }
            finally
            {
                window!.FindFirstDescendant(cf => cf.ByAutomationId("btnCloseKit"))?.AsButton().Invoke();
            }
        }
    }
}
