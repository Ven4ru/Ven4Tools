using System;
using System.IO;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Ven4Tools.ClientUITests
{
    /// <summary>
    /// Проверка твиков после обновления Windows: вкладка «Очистка» сверяет применённые
    /// твики с текущим состоянием системы и показывает полосу, если какой-то из них
    /// больше не действует.
    ///
    /// Систему тест не меняет. В журнал применённого заранее записывается твик
    /// «Рекламный идентификатор», а ожидание выводится из того, что сейчас стоит в
    /// реестре: значение 0 — твик действует и полосы нет, любое другое — полоса есть.
    /// </summary>
    [TestClass]
    public class DebloatDriftUiTests
    {
        private const string TweakId = "advertising_id";
        private const string RegistrySubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo";
        private const string RegistryValue = "Enabled";

        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
        private static readonly string JournalPath = Path.Combine(SettingsDir, "debloat_applied.json");

        private static string? _journalBackup;
        private static bool _journalExisted;
        private static AppSession? _session;
        private static string? _launchError;

        private static bool TweakIsInEffect()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistrySubKey);
            return key?.GetValue(RegistryValue) is int value && value == 0;
        }

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            Directory.CreateDirectory(SettingsDir);
            _journalExisted = File.Exists(JournalPath);
            if (_journalExisted) _journalBackup = File.ReadAllText(JournalPath);

            File.WriteAllText(JournalPath, "{\"" + TweakId + "\":\"" + DateTime.UtcNow.ToString("o") + "\"}");

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
                if (_journalExisted && _journalBackup != null) File.WriteAllText(JournalPath, _journalBackup);
                else if (File.Exists(JournalPath)) File.Delete(JournalPath);
            }
            catch { /* восстановление — по возможности */ }
        }

        [TestMethod]
        public void ПолосаНеДействующихТвиков_СоответствуетСостояниюРеестра()
        {
            if (_session == null)
                Assert.Inconclusive("Клиент Ven4Tools не запущен: " + (_launchError ?? "причина неизвестна"));
            var s = _session!;
            bool inEffect = TweakIsInEffect();

            UiNav.Find(s, "btnDebloaterTab")!.AsButton().Invoke();

            Assert.IsNotNull(
                Retry.WhileNull(() => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCheckDrift")),
                    timeout: TimeSpan.FromSeconds(10), interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Result,
                "На вкладке «Очистка» нет кнопки «Проверить применённое».");

            AutomationElement? Reapply() => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnReapplyDrifted"));
            bool BannerShown() => Reapply() is { IsOffscreen: false };

            if (inEffect)
            {
                // Проверка идёт при открытии вкладки; даём ей время и убеждаемся, что полоса не появилась.
                Assert.IsFalse(
                    Retry.WhileFalse(BannerShown, timeout: TimeSpan.FromSeconds(4),
                        interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Success,
                    "Твик действует (значение реестра 0), а полоса «не действуют» показана.");
            }
            else
            {
                Assert.IsTrue(
                    Retry.WhileFalse(BannerShown, timeout: TimeSpan.FromSeconds(10),
                        interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Success,
                    "Твик не действует (значение реестра не 0), а полосы «не действуют» нет.");

                var text = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("txtDrift"));
                Assert.IsNotNull(text, "В полосе нет текста.");
                StringAssert.Contains(text!.Name, "Рекламный идентификатор",
                    "В полосе не назван твик, который перестал действовать.");
            }

            // Повторная проверка по кнопке ничего в системе не меняет и состояние полосы сохраняет.
            UiNav.Find(s, "btnCheckDrift")!.AsButton().Invoke();
            Assert.IsTrue(
                Retry.WhileFalse(() => BannerShown() == !inEffect, timeout: TimeSpan.FromSeconds(10),
                    interval: TimeSpan.FromMilliseconds(300), throwOnTimeout: false).Success,
                "После «Проверить применённое» состояние полосы не соответствует реестру.");
        }
    }
}
