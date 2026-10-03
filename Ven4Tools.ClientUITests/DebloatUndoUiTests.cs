using System;
using System.IO;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Ven4Tools.ClientUITests
{
    /// <summary>
    /// Точечный откат твика «Очистки»: кнопка «Вернуть» появляется у твика, для которого
    /// сохранено прежнее состояние, и после отката исчезает.
    ///
    /// Сам твик тест не применяет. Запись отката готовится заранее и хранит ТЕКУЩЕЕ
    /// значение реестра, поэтому «возврат» записывает то, что там уже стоит, — система
    /// после теста остаётся такой же, какой была.
    /// </summary>
    [TestClass]
    public class DebloatUndoUiTests
    {
        private const string TweakId = "advertising_id";
        private const string RegistrySubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo";
        private const string RegistryValue = "Enabled";

        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
        private static readonly string UndoPath = Path.Combine(SettingsDir, "debloat_undo.json");

        private static string? _undoBackup;
        private static bool _undoExisted;
        private static AppSession? _session;
        private static string? _launchError;

        private static (bool Exists, int Value) ReadCurrent()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistrySubKey);
            return key?.GetValue(RegistryValue) is int value ? (true, value) : (false, 0);
        }

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            Directory.CreateDirectory(SettingsDir);
            _undoExisted = File.Exists(UndoPath);
            if (_undoExisted) _undoBackup = File.ReadAllText(UndoPath);

            var (exists, value) = ReadCurrent();
            File.WriteAllText(UndoPath,
                "{\"" + TweakId + "\":{\"AppliedUtc\":\"" + DateTime.UtcNow.ToString("o") + "\"," +
                "\"Registry\":[{\"Path\":\"HKCU:\\\\" + RegistrySubKey.Replace("\\", "\\\\") + "\"," +
                "\"Name\":\"" + RegistryValue + "\",\"Existed\":" + (exists ? "true" : "false") + ",\"Value\":" + value + "}]," +
                "\"Services\":[]}}");

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
                if (_undoExisted && _undoBackup != null) File.WriteAllText(UndoPath, _undoBackup);
                else if (File.Exists(UndoPath)) File.Delete(UndoPath);
            }
            catch { /* восстановление — по возможности */ }
        }

        [TestMethod]
        public void КнопкаВернуть_ЕстьУТвикаСЗаписью_ИПослеОткатаИсчезает()
        {
            if (_session == null)
                Assert.Inconclusive("Клиент Ven4Tools не запущен: " + (_launchError ?? "причина неизвестна"));
            var s = _session!;
            var before = ReadCurrent();

            UiNav.Find(s, "btnDebloaterTab")!.AsButton().Invoke();

            AutomationElement? Undo() => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnUndoTweak_" + TweakId));
            var undo = Retry.WhileNull(Undo, timeout: TimeSpan.FromSeconds(10),
                interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(undo, "У твика с сохранённым состоянием нет кнопки «Вернуть».");

            // У твика без записи кнопки быть не должно.
            Assert.IsNull(s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnUndoTweak_telemetry")),
                "Кнопка «Вернуть» показана у твика, который не применялся.");

            undo!.AsButton().Invoke();

            var confirm = Retry.WhileNull(() => s.MainWindow.ModalWindows.FirstOrDefault(),
                timeout: TimeSpan.FromSeconds(8), interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result;
            Assert.IsNotNull(confirm, "Откат твика должен спрашивать подтверждение.");
            var yes = confirm!.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => b.Name is "Да" or "Yes");
            Assert.IsNotNull(yes, "В подтверждении отката нет кнопки «Да».");
            yes!.AsButton().Invoke();

            Assert.IsTrue(
                Retry.WhileTrue(() => Undo() is { IsOffscreen: false }, timeout: TimeSpan.FromSeconds(15),
                    interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Success,
                "После отката кнопка «Вернуть» должна исчезнуть.");

            StringAssert.DoesNotMatch(File.ReadAllText(UndoPath), new System.Text.RegularExpressions.Regex(TweakId),
                "Запись отката должна быть удалена после успешного возврата.");
            Assert.AreEqual(before, ReadCurrent(), "Откат записал в реестр не то значение, что было сохранено.");
        }
    }
}
