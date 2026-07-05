using System;
using System.IO;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ven4Tools.ClientUITests
{
    /// <summary>
    /// Проверяет сегодняшний MEDIUM-фикс: раньше обе fallback-точки
    /// CatalogLoaderService при OfflineMode возвращали пустой каталог вместо
    /// содержимого embedded_catalog.json. Тест форсирует OfflineMode=true и
    /// гарантирует отсутствие дискового кэша (Data/master.json) — тогда
    /// единственный путь для CatalogLoaderService — это по-настоящему прочитать
    /// встроенный ресурс, а не притвориться пустым каталогом.
    ///
    /// profile.json пишется напрямую (OfflineMode=true, HasSelectedCategory=true —
    /// заодно обходит мастер первого запуска, не про него этот сценарий).
    /// Дисковый кэш каталога (Data/master.json[.sig] рядом с exe) бэкапится и
    /// восстанавливается, чтобы не задеть реальное состояние машины.
    /// </summary>
    [TestClass]
    public class OfflineEmbeddedCatalogUiTests
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
        private static readonly string ProfilePath = Path.Combine(SettingsDir, "profile.json");

        private static readonly string CacheDir = Path.Combine(
            AppSession.ResolveClientExePath() is var exe && !string.IsNullOrEmpty(exe)
                ? Path.GetDirectoryName(exe)!
                : AppContext.BaseDirectory,
            "Data");
        private static readonly string CacheCatalogPath = Path.Combine(CacheDir, "master.json");
        private static readonly string CacheSigPath = Path.Combine(CacheDir, "master.json.sig");
        private static readonly string CacheBackupDir = Path.Combine(Path.GetTempPath(), "ven4tools_cache_backup_" + Guid.NewGuid().ToString("N"));

        private static string? _profileBackup;
        private static bool _profileExistedBefore;
        private static bool _cacheExistedBefore;
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
                "{\"CatalogMode\":\"full\",\"HasSelectedCategory\":true,\"OfflineMode\":true,\"ForceOnlineMode\":false}");

            _cacheExistedBefore = File.Exists(CacheCatalogPath);
            if (_cacheExistedBefore)
            {
                Directory.CreateDirectory(CacheBackupDir);
                File.Copy(CacheCatalogPath, Path.Combine(CacheBackupDir, "master.json"), true);
                if (File.Exists(CacheSigPath))
                    File.Copy(CacheSigPath, Path.Combine(CacheBackupDir, "master.json.sig"), true);
                File.Delete(CacheCatalogPath);
                if (File.Exists(CacheSigPath)) File.Delete(CacheSigPath);
            }

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
                if (_profileExistedBefore)
                    File.WriteAllText(ProfilePath, _profileBackup!);
                else if (File.Exists(ProfilePath))
                    File.Delete(ProfilePath);
            }
            catch { }

            try
            {
                if (_cacheExistedBefore)
                {
                    File.Copy(Path.Combine(CacheBackupDir, "master.json"), CacheCatalogPath, true);
                    var sigBackup = Path.Combine(CacheBackupDir, "master.json.sig");
                    if (File.Exists(sigBackup)) File.Copy(sigBackup, CacheSigPath, true);
                    try { Directory.Delete(CacheBackupDir, true); } catch { }
                }
                else
                {
                    if (File.Exists(CacheCatalogPath)) File.Delete(CacheCatalogPath);
                    if (File.Exists(CacheSigPath)) File.Delete(CacheSigPath);
                }
            }
            catch { }
        }

        private static AppSession Require()
        {
            if (_session == null)
                Assert.Inconclusive("Клиент не запущен: " + (_launchError ?? "неизвестная причина"));
            return _session!;
        }

        [TestMethod]
        public void ОфлайнРежим_БезКэша_ПоказываетВстроенныйКаталогНеПустой()
        {
            var s = Require();

            var catalogBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnCatalogTab"));
            Assert.IsNotNull(catalogBtn, "Не найдена кнопка вкладки «Каталог».");
            catalogBtn!.AsButton().Invoke();

            var checkBoxes = Retry.WhileEmpty(
                () => s.MainWindow.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.CheckBox)),
                timeout: ElementTimeout,
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false).Result;

            Assert.IsTrue(checkBoxes != null && checkBoxes.Length > 0,
                "В офлайн-режиме без дискового кэша каталог пуст — embedded_catalog.json не читается " +
                "(регрессия сегодняшнего MEDIUM-фикса CatalogLoaderService).");

            // Разумная нижняя граница — заведомо меньше реальных ~71, но достаточно,
            // чтобы отличить «реально прочитанный каталог» от одного случайного элемента.
            Assert.IsTrue(checkBoxes!.Length >= 20,
                $"Найдено подозрительно мало приложений в офлайн-каталоге: {checkBoxes.Length} (ожидалось ~71).");

            // Заодно проверяем сам текст из embedded_catalog.json (не статичные
            // заголовки Expander'ов из XAML — те захардкожены и мойибаке не ловят):
            // названия приложений в чекбоксах реально приходят из JSON-каталога.
            var appNames = checkBoxes!
                .Select(cb => cb.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))?.Name ?? "")
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();
            Assert.IsTrue(appNames.Any(t => t.Contains("AIDA64") || t.Contains("AutoHotkey") || t.Contains("Firefox")),
                "Среди названий приложений офлайн-каталога не нашлось ни одного ожидаемого — " +
                "возможно, embedded_catalog.json не прочитался корректно. Найдено: " +
                string.Join(" | ", appNames.Take(15)));
            Assert.IsFalse(appNames.Any(t => t.Contains("�")),
                "В названиях приложений офлайн-каталога есть символ повреждения (�) — возможно, снова битая кодировка. " +
                "Найдено: " + string.Join(" | ", appNames.Take(15)));
        }
    }
}
