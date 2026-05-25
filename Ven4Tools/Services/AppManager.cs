using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
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
                    hiddenApps = JsonSerializer.Deserialize<HashSet<string>>(json) ?? new HashSet<string>();
                }
            }
            catch { }
        }

        private void SaveHiddenApps()
        {
            try
            {
                string? directory = Path.GetDirectoryName(hiddenAppsPath);
                if (directory != null)
                    Directory.CreateDirectory(directory);
                    
                File.WriteAllText(hiddenAppsPath, 
                    JsonSerializer.Serialize(hiddenApps, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public void HideStandardApp(string appId)
        {
            hiddenApps.Add(appId);
            SaveHiddenApps();
        }

        public bool IsAppHidden(string appId) => hiddenApps.Contains(appId);

public void LoadAlternativeSources()
{
    try
    {
        if (File.Exists(alternativesPath))
        {
            var json = File.ReadAllText(alternativesPath);
            alternatives = JsonSerializer.Deserialize<Dictionary<string, AlternativeSource>>(json) 
                ?? new Dictionary<string, AlternativeSource>();
            
            // Применяем альтернативные источники к приложениям
            foreach (var kvp in alternatives)
            {
                var app = apps.FirstOrDefault(a => a.Id == kvp.Key);
                if (app != null)
                {
                    if (!string.IsNullOrEmpty(kvp.Value.WingetId))
                    {
                        app.AlternativeId = kvp.Value.WingetId;
                        System.Diagnostics.Debug.WriteLine($"Applied alternative winget ID {kvp.Value.WingetId} to {app.DisplayName}");
                        
                        if (kvp.Value.Priority)
                        {
                            // Приоритетный источник
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(kvp.Value.Url) && !app.InstallerUrls.Contains(kvp.Value.Url))
                    {
                        if (kvp.Value.UrlPriority)
                            app.InstallerUrls.Insert(0, kvp.Value.Url);
                        else
                            app.InstallerUrls.Add(kvp.Value.Url);
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"App {kvp.Key} not found for alternative source");
                }
            }
        }
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"Error loading alternatives: {ex.Message}");
    }
}

        public void SaveAlternativeSource(string appId, string? wingetId, string? url, bool priority = false)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"SaveAlternativeSource called: appId={appId}, wingetId={wingetId}, url={url}");
                System.Diagnostics.Debug.WriteLine($"Alternatives path: {alternativesPath}");

                if (!alternatives.ContainsKey(appId))
                {
                    alternatives[appId] = new AlternativeSource();
                }

                if (!string.IsNullOrEmpty(wingetId))
                {
                    alternatives[appId].WingetId = wingetId;
                    alternatives[appId].Priority = priority;
                    alternatives[appId].LastUpdated = DateTime.Now;

                    var app = apps.FirstOrDefault(a => a.Id == appId);
                    if (app != null)
                    {
                        app.AlternativeId = wingetId;
                        System.Diagnostics.Debug.WriteLine($"Applied AlternativeId={wingetId} to {app.DisplayName}");
                    }
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

                SaveAlternatives();

                // Проверяем, что файл создался
                if (File.Exists(alternativesPath))
                {
                    var content = File.ReadAllText(alternativesPath);
                    System.Diagnostics.Debug.WriteLine($"Alternatives saved, file size: {content.Length}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"ERROR: Alternatives file not created at {alternativesPath}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving alternative: {ex.Message}");
            }
        }

        public void IncrementAlternativeSuccess(string appId)
        {
            try
            {
                if (alternatives.ContainsKey(appId))
                {
                    alternatives[appId].SuccessCount++;
                    SaveAlternatives();
                }
            }
            catch { }
        }

        public void RemoveAlternativeSource(string appId)
        {
            try
            {
                if (alternatives.Remove(appId))
                {
                    var app = apps.FirstOrDefault(a => a.Id == appId);
                    if (app != null)
                    {
                        app.AlternativeId = null;
                    }
                    
                    SaveAlternatives();
                }
            }
            catch { }
        }

        public Dictionary<string, AlternativeSource> GetAllAlternatives() => alternatives;

private void SaveAlternatives()
{
    try
    {
        string? directory = Path.GetDirectoryName(alternativesPath);
        if (directory != null)
            Directory.CreateDirectory(directory);
            
        var json = JsonSerializer.Serialize(alternatives, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(alternativesPath, json);
        
        System.Diagnostics.Debug.WriteLine($"Saved alternatives to: {alternativesPath}");
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"Error saving alternatives: {ex.Message}");
    }
}

        public List<AppInfo> GetAllApps() => apps
            .Where(a => !hiddenApps.Contains(a.Id))
            .OrderBy(a => a.Category)
            .ThenBy(a => a.DisplayName)
            .ToList();

        public AppInfo? GetAppById(string appId)
        {
            return apps.FirstOrDefault(a => a.Id == appId);
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
                string? directory = Path.GetDirectoryName(selectionPath);
                if (directory != null)
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(selectionPath, JsonSerializer.Serialize(settings));
            }
            catch { }
        }

        public List<string> LoadSelectedApps()
        {
            try
            {
                string selectionPath = configPath + ".selection";
                if (File.Exists(selectionPath))
                {
                    var json = File.ReadAllText(selectionPath);
                    var settings = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                    if (settings != null && settings.ContainsKey("SelectedApps"))
                    {
                        var element = (JsonElement)settings["SelectedApps"];
                        return element.EnumerateArray()
                            .Select(x => x.GetString())
                            .Where(x => x != null)
                            .Select(x => x!)
                            .ToList();
                    }
                }
            }
            catch { }
            return new List<string>();
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
            catch { }
        }

        private void SaveUserApps()
        {
            try
            {
                var userApps = apps.Where(a => a.IsUserAdded).ToList();
                string? directory = Path.GetDirectoryName(configPath);
                if (directory != null)
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(configPath, JsonSerializer.Serialize(userApps, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

public long GetTotalRequiredSpace(List<string> selectedIds)
{
    // Теперь размер будет браться из динамической проверки
    // Этот метод можно оставить как запасной вариант
    return 0;
}
    }
}