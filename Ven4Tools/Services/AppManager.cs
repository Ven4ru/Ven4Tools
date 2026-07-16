using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Ven4Tools.Helpers;
using Ven4Tools.Models;
using System.Diagnostics;

namespace Ven4Tools.Services
{
    public class AppManager
    {
        private readonly string configPath;
        private readonly string alternativesPath;
        private readonly string hiddenAppsPath;
        private List<AppInfo> apps;
        private readonly object lockObj = new object();
        private readonly bool isPortable;
        private Dictionary<string, AlternativeSource> alternatives = new();
        private HashSet<string> hiddenApps = new();

        public AppManager()
        {
            isPortable = DetectPortableMode();
            configPath = GetConfigPath();
            alternativesPath = Path.Combine(
                Path.GetDirectoryName(configPath)!,
                "alternatives.json");
            hiddenAppsPath = Path.Combine(
                Path.GetDirectoryName(configPath)!,
                "hidden.json");
            apps = new List<AppInfo>();
            LoadUserApps();
            LoadAlternativeSources();
            LoadHiddenApps();
        }
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

        public void ApplyAlternativesToCatalog(MasterCatalog catalog)
        {
            if (catalog?.Apps == null || catalog.Apps.Count == 0)
                return;

            lock (lockObj)
            {
                foreach (var catalogApp in catalog.Apps)
                {
                    if (alternatives.TryGetValue(catalogApp.Id, out var alt))
                    {
                        if (!string.IsNullOrEmpty(alt.WingetId))
                            catalogApp.WingetId = alt.WingetId;
                        if (!string.IsNullOrEmpty(alt.Url))
                            catalogApp.DownloadUrl = alt.Url;
                    }
                }
            }
        }

        private bool DetectPortableMode()
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

        private string GetConfigPath()
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

        private void LoadHiddenApps()
        {
            try
            {
                if (File.Exists(hiddenAppsPath))
                {
                    var json = File.ReadAllText(hiddenAppsPath);
                    var loaded = JsonConvert.DeserializeObject<HashSet<string>>(json) ?? new HashSet<string>();
                    lock (lockObj) { hiddenApps = loaded; }
                }
            }
            catch (Exception ex) { AppLogger.Write($"[AppManager] LoadHiddenApps: {ex.Message}"); }
        }

        private void SaveHiddenApps()
        {
            try
            {
                string json;
                lock (lockObj) { json = JsonConvert.SerializeObject(hiddenApps, Formatting.Indented); }
                FileHelper.WriteAllTextAtomic(hiddenAppsPath, json);
            }
            catch (Exception ex) { AppLogger.Write($"[AppManager] SaveHiddenApps: {ex.Message}"); }
        }

        public bool IsAppHidden(string appId) { lock (lockObj) { return hiddenApps.Contains(appId); } }

        public void LoadAlternativeSources()
        {
            try
            {
                if (File.Exists(alternativesPath))
                {
                    var json = File.ReadAllText(alternativesPath);
                    var loaded = JsonConvert.DeserializeObject<Dictionary<string, AlternativeSource>>(json)
                        ?? new Dictionary<string, AlternativeSource>();

                    lock (lockObj)
                    {
                        alternatives = loaded;

                        // Применяем альтернативные источники к приложениям
                        foreach (var kvp in alternatives)
                        {
                            var app = apps.FirstOrDefault(a => a.Id == kvp.Key);
                            if (app != null)
                            {
                                if (!string.IsNullOrEmpty(kvp.Value.WingetId))
                                {
                                    app.AlternativeId = kvp.Value.WingetId;
                                }

                                if (!string.IsNullOrEmpty(kvp.Value.Url) && !app.InstallerUrls.Contains(kvp.Value.Url))
                                {
                                    if (kvp.Value.UrlPriority)
                                        app.InstallerUrls.Insert(0, kvp.Value.Url);
                                    else
                                        app.InstallerUrls.Add(kvp.Value.Url);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { AppLogger.Write($"[AppManager] LoadAlternativeSources: {ex.Message}"); }
        }

        public void SaveAlternativeSource(string appId, string? wingetId, string? url, bool priority = false)
        {
            try
            {
                lock (lockObj)
                {
                    if (!alternatives.ContainsKey(appId))
                        alternatives[appId] = new AlternativeSource();

                    if (!string.IsNullOrEmpty(wingetId))
                    {
                        alternatives[appId].WingetId = wingetId;
                        alternatives[appId].Priority = priority;
                        alternatives[appId].LastUpdated = DateTime.Now;

                        var app = apps.FirstOrDefault(a => a.Id == appId);
                        if (app != null)
                            app.AlternativeId = wingetId;
                    }

                    if (!string.IsNullOrEmpty(url))
                    {
                        alternatives[appId].Url = url;
                        alternatives[appId].UrlPriority = priority;
                        alternatives[appId].LastUpdated = DateTime.Now;

                        var app = apps.FirstOrDefault(a => a.Id == appId);
                        if (app != null && !app.InstallerUrls.Contains(url))
                        {
                            if (priority)
                                app.InstallerUrls.Insert(0, url);
                            else
                                app.InstallerUrls.Add(url);
                        }
                    }
                }

                SaveAlternatives();
            }
            catch (Exception ex) { AppLogger.Write($"[AppManager] SaveAlternativeSource: {ex.Message}"); }
        }

        public void RemoveAlternativeSource(string appId)
        {
            try
            {
                bool removed = false;
                lock (lockObj)
                {
                    if (alternatives.Remove(appId))
                    {
                        var app = apps.FirstOrDefault(a => a.Id == appId);
                        if (app != null)
                            app.AlternativeId = null;
                        removed = true;
                    }
                }
                if (removed) SaveAlternatives();
            }
            catch (Exception ex) { AppLogger.Write($"[AppManager] RemoveAlternativeSource: {ex.Message}"); }
        }

        private void SaveAlternatives()
        {
            try
            {
                string json;
                lock (lockObj) { json = JsonConvert.SerializeObject(alternatives, Formatting.Indented); }
                FileHelper.WriteAllTextAtomic(alternativesPath, json);
            }
            catch (Exception ex) { AppLogger.Write($"[AppManager] SaveAlternatives: {ex.Message}"); }
        }

        public List<AppInfo> GetAllApps()
        {
            List<AppInfo> snapshot;
            HashSet<string> hiddenSnapshot;
            lock (lockObj)
            {
                snapshot = apps.ToList();
                hiddenSnapshot = new HashSet<string>(hiddenApps);
            }
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

        // apps.json защищается через DPAPI (привязка к учётной записи Windows) только
        // в обычном режиме — файл в user-writable %LocalAppData% иначе можно подменить
        // другим процессом/пользователем, а клиент прочитал бы подделанные URL/аргументы
        // установки без проверки целостности. В переносимом режиме файл лежит рядом с exe
        // и обязан переноситься между машинами/учётками — DPAPI это сломало бы, поэтому
        // там сохраняется обычный JSON (модель угроз переносимого носителя иная).
        private bool ProtectUserApps => !isPortable;

        // Дополнительная энтропия DPAPI: привязывает защищённый blob именно к этому
        // назначению, а не к любому DPAPI-контейнеру той же учётной записи.
        private static readonly byte[] AppsEntropy = Encoding.UTF8.GetBytes("Ven4Tools.apps.v1");

        private void LoadUserApps()
        {
            try
            {
                if (!File.Exists(configPath)) return;
                var raw = File.ReadAllText(configPath);
                if (string.IsNullOrWhiteSpace(raw)) return;

                List<AppInfo>? userApps;
                bool needsUpgrade = false;

                if (ProtectUserApps)
                {
                    // Сначала пробуем снять DPAPI-защиту. Неудача означает legacy-формат
                    // (голый JSON от прежних версий): принимаем его и помечаем на миграцию —
                    // при следующей записи файл будет пересохранён уже защищённым.
                    userApps = TryUnprotectUserApps(raw);
                    if (userApps == null)
                    {
                        userApps = TryParsePlainUserApps(raw);
                        if (userApps != null) needsUpgrade = true;
                    }
                }
                else
                {
                    userApps = TryParsePlainUserApps(raw);
                }

                if (userApps != null)
                {
                    apps.AddRange(userApps);
                    if (needsUpgrade)
                    {
                        AppLogger.Write("[AppManager] apps.json в старом незащищённом формате — миграция в DPAPI");
                        SaveUserApps();
                    }
                }
            }
            catch (Exception ex) { AppLogger.Write($"[AppManager] LoadUserApps: {ex.Message}"); }
        }

        private static List<AppInfo>? TryUnprotectUserApps(string raw)
        {
            try
            {
                var protectedBytes = Convert.FromBase64String(raw.Trim());
                var plainBytes = ProtectedData.Unprotect(
                    protectedBytes, AppsEntropy, DataProtectionScope.CurrentUser);
                return JsonConvert.DeserializeObject<List<AppInfo>>(Encoding.UTF8.GetString(plainBytes));
            }
            catch { return null; }
        }

        private static List<AppInfo>? TryParsePlainUserApps(string raw)
        {
            try { return JsonConvert.DeserializeObject<List<AppInfo>>(raw); }
            catch { return null; }
        }

        private string SerializeUserApps(List<AppInfo> userApps)
        {
            var json = JsonConvert.SerializeObject(userApps, Formatting.Indented);
            if (!ProtectUserApps) return json;
            var protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(json), AppsEntropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        private void SaveUserApps()
        {
            try
            {
                var userApps = apps.Where(a => a.IsUserAdded).ToList();
                FileHelper.WriteAllTextAtomic(configPath, SerializeUserApps(userApps));
            }
            catch (Exception ex) { AppLogger.Write($"[AppManager] SaveUserApps: {ex.Message}"); }
        }

    }
}