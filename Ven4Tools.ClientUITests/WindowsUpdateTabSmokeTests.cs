using System;
using System.IO;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ven4Tools.ClientUITests
{
    /// <summary>
    /// Смоук-тест вкладки Windows Update: только то, что она открывается и
    /// отрисовывается без падения. Реальная установка патчей здесь не
    /// вызывается никогда (см. Global Constraints плана) — устанавливать
    /// патчи ОС в CI-раннере небезопасно и непредсказуемо по времени.
    ///
    /// WindowsUpdateMode подсеивается в profile.json заранее ("NotifyOnly"),
    /// чтобы обойти модальный диалог первого входа — тот же приём, что уже
    /// использует OfflineEmbeddedCatalogUiTests для мастера выбора категории.
    /// </summary>
    [TestClass]
    public class WindowsUpdateTabSmokeTests
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
        private static readonly string ProfilePath = Path.Combine(SettingsDir, "profile.json");

        private static string? _profileBackup;
        private static bool _profileExistedBefore;
        private static AppSession? _session;
        private static string? _launchError;

        private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(15);

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            _profileExistedBefore = File.Exists(ProfilePath);
            if (_profileExistedBefore)
                _profileBackup = File.ReadAllText(ProfilePath);

            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(ProfilePath,
                "{\"CatalogMode\":\"full\",\"HasSelectedCategory\":true,\"WindowsUpdateMode\":\"NotifyOnly\"}");

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

            if (_profileExistedBefore) File.WriteAllText(ProfilePath, _profileBackup!);
            else if (File.Exists(ProfilePath)) File.Delete(ProfilePath);
        }

        private static AppSession Require()
        {
            if (_session == null)
            {
                Assert.Inconclusive(
                    "Клиент Ven4Tools не запущен, UI-тесты пропущены. Причина: " +
                    (_launchError ?? "неизвестна") +
                    ". Запустите тесты в интерактивной сессии «от имени администратора».");
            }
            return _session!;
        }

        [TestMethod]
        public void WindowsUpdate_ВкладкаОткрываетсяИПоказываетСтатус()
        {
            var s = Require();

            var navBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnWindowsUpdateTab"));
            Assert.IsNotNull(navBtn, "Не найдена кнопка навигации 'btnWindowsUpdateTab'.");
            navBtn!.AsButton().Invoke();

            // txtStatus существует всегда (см. WindowsUpdateTab.xaml) — сразу после
            // открытия там будет "⏳ Проверка обновлений..." либо уже готовый результат,
            // если проверка успела завершиться быстро. Главное — что элемент есть и
            // вкладка не упала при открытии.
            var status = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("txtStatus")),
                timeout: ElementTimeout,
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false).Result;

            Assert.IsNotNull(status, "Элемент статуса (txtStatus) вкладки Windows Update не отобразился.");
        }
    }
}
