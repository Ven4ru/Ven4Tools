using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
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
                    var loaded = JsonSerializer.Deserialize<HashSet<string>>(json) ?? new HashSet<string>();
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
                lock (lockObj) { json = JsonSerializer.Serialize(hiddenApps, new JsonSerializerOptions { WriteIndented = true }); }
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
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, AlternativeSource>>(json)
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
                lock (lockObj) { json = JsonSerializer.Serialize(alternatives, new JsonSerializerOptions { WriteIndented = true }); }
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

        public void SaveSelectedApps(List<string> selectedIds)
        {
            try
            {
                var settings = new Dictionary<string, object>
                {
                    { "SelectedApps", selectedIds },
                    { "LastUpdated", DateTime.Now }
                };
                
                string selectionPath = configPath + ".selection";
                FileHelper.WriteAllTextAtomic(selectionPath, JsonSerializer.Serialize(settings));
            }
            catch (Exception ex) { AppLogger.Write($"[AppManager] SaveSelectedApps: {ex.Message}"); }
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

        private void LoadUserApps()
        {
            try
            {
                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    var userApps = JsonSerializer.Deserialize<List<AppInfo>>(json);
                    if (userApps != null)
                    {
                        apps.AddRange(userApps);
                    }
                }
            }
            catch (Exception ex) { AppLogger.Write($"[AppManager] LoadUserApps: {ex.Message}"); }
        }

        private void SaveUserApps()
        {
            try
            {
                var userApps = apps.Where(a => a.IsUserAdded).ToList();
                FileHelper.WriteAllTextAtomic(configPath, JsonSerializer.Serialize(userApps, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AppLogger.Write($"[AppManager] SaveUserApps: {ex.Message}"); }
        }

    }
}