using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Единый список приложений клиента (каталожные + добавленные пользователем) и фасад
    /// над тремя независимыми хранилищами состояния, которые раньше жили прямо здесь под
    /// одним общим замком:
    /// <list type="bullet">
    /// <item><see cref="UserAppsStore"/> — apps.json с DPAPI-защитой;</item>
    /// <item><see cref="AlternativeSourceStore"/> — alternatives.json;</item>
    /// <item><see cref="HiddenAppsStore"/> — hidden.json.</item>
    /// </list>
    /// Здесь остаётся только то, что действительно принадлежит списку приложений:
    /// где лежат файлы конфигурации, сам список и композиция результата из хранилищ.
    /// Публичный интерфейс не менялся — вызывающий код (CatalogViewModel) не тронут.
    /// </summary>
    public class AppManager
    {
        private readonly List<AppInfo> apps;
        private readonly object lockObj = new object();

        private readonly UserAppsStore userAppsStore;
        private readonly AlternativeSourceStore alternativeSourceStore;
        private readonly HiddenAppsStore hiddenAppsStore;

        public AppManager()
        {
            bool isPortable = DetectPortableMode();
            string configPath = GetConfigPath(isPortable);
            string configDir = Path.GetDirectoryName(configPath)!;

            userAppsStore = new UserAppsStore(configPath, protect: !isPortable);
            alternativeSourceStore = new AlternativeSourceStore(Path.Combine(configDir, "alternatives.json"));
            hiddenAppsStore = new HiddenAppsStore(Path.Combine(configDir, "hidden.json"));

            apps = userAppsStore.Load();
            LoadAlternativeSources();
        }

        // ── Список приложений ────────────────────────────────────────────────────

        public void AddCatalogApp(AppInfo app)
        {
            lock (lockObj)
            {
                var existing = apps.FirstOrDefault(a => a.Id == app.Id);
                if (existing == null)
                {
                    apps.Add(app);
                }
                else
                {
                    existing.DisplayName = app.DisplayName;
                    existing.Category = app.Category;
                    existing.InstallerUrls = app.InstallerUrls;
                    existing.AlternativeId = app.AlternativeId;
                    // SHA256 обязателен для верификации Direct-источника, а Choco
                    // идентификатор — для соответствующего источника: без синхронизации
                    // при обновлении каталога эти поля терялись у уже добавленного приложения.
                    existing.Sha256 = app.Sha256;
                    existing.ChocoId = app.ChocoId;
                    if (!string.IsNullOrEmpty(app.SilentArgs))
                        existing.SilentArgs = app.SilentArgs;
                }
            }
        }

        public List<AppInfo> GetAllApps()
        {
            List<AppInfo> snapshot;
            lock (lockObj) { snapshot = apps.ToList(); }

            var hiddenSnapshot = hiddenAppsStore.Snapshot();
            return snapshot
                .Where(a => !hiddenSnapshot.Contains(a.Id))
                .OrderBy(a => a.Category)
                .ThenBy(a => a.DisplayName)
                .ToList();
        }

        public AppInfo? GetAppById(string appId)
        {
            lock (lockObj)
            {
                return apps.FirstOrDefault(a => a.Id == appId);
            }
        }

        // ── Приложения, добавленные пользователем ────────────────────────────────

        public void AddUserApp(AppInfo app)
        {
            lock (lockObj)
            {
                app.IsUserAdded = true;
                apps.Add(app);
                SaveUserApps();
            }
        }

        public void ClearUserApps()
        {
            lock (lockObj)
            {
                apps.RemoveAll(a => a.IsUserAdded);
                SaveUserApps();
            }
        }

        public void RemoveUserApp(string appId)
        {
            lock (lockObj)
            {
                var app = apps.FirstOrDefault(a => a.Id == appId && a.IsUserAdded);
                if (app != null)
                {
                    apps.Remove(app);
                    SaveUserApps();
                    RemoveAlternativeSource(appId);
                }
            }
        }

        private void SaveUserApps()
        {
            lock (lockObj)
            {
                userAppsStore.Save(apps.Where(a => a.IsUserAdded).ToList());
            }
        }

        // ── Альтернативные источники ─────────────────────────────────────────────

        public void LoadAlternativeSources()
        {
            alternativeSourceStore.Reload();
            lock (lockObj) { alternativeSourceStore.ApplyToApps(apps); }
        }

        public void ApplyAlternativesToCatalog(MasterCatalog catalog)
        {
            alternativeSourceStore.ApplyToCatalog(catalog);
        }

        public void SaveAlternativeSource(string appId, string? wingetId, string? url, bool priority = false)
        {
            alternativeSourceStore.Set(appId, wingetId, url, priority);

            lock (lockObj)
            {
                var app = apps.FirstOrDefault(a => a.Id == appId);
                if (app != null)
                    AlternativeSourceStore.ApplyOverride(app, wingetId, url, priority);
            }
        }

        public void RemoveAlternativeSource(string appId)
        {
            if (!alternativeSourceStore.Remove(appId)) return;

            lock (lockObj)
            {
                var app = apps.FirstOrDefault(a => a.Id == appId);
                if (app != null)
                    app.AlternativeId = null;
            }
        }

        // ── Скрытые приложения ───────────────────────────────────────────────────

        public bool IsAppHidden(string appId) => hiddenAppsStore.IsHidden(appId);

        // ── Расположение файлов конфигурации ─────────────────────────────────────

        private static bool DetectPortableMode()
        {
            try
            {
                string? exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (exeDir == null) return false;

                string portableMarker = Path.Combine(exeDir, "portable.dat");
                return File.Exists(portableMarker);
            }
            catch
            {
                return false;
            }
        }

        private static string GetConfigPath(bool isPortable)
        {
            if (isPortable)
            {
                string? exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (exeDir == null) throw new InvalidOperationException("Не удалось определить путь к исполняемому файлу");

                string dataDir = Path.Combine(exeDir, "Data");
                Directory.CreateDirectory(dataDir);
                return Path.Combine(dataDir, "apps.json");
            }
            else
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string dataDir = Path.Combine(appData, "Ven4Tools");
                Directory.CreateDirectory(dataDir);
                return Path.Combine(dataDir, "apps.json");
            }
        }
    }
}
