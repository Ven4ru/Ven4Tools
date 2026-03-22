using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Ven4Tools.Models;

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
            apps = GetDefaultApps();
            LoadUserApps();
            LoadAlternativeSources();
            LoadHiddenApps();
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

private void LoadAlternativeSources()
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
                        Console.WriteLine($"Applied alternative winget ID {kvp.Value.WingetId} to {app.DisplayName}");
                        
                        // Добавляем в InstallerUrls если есть приоритет
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
                    Console.WriteLine($"App {kvp.Key} not found for alternative source");
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error loading alternatives: {ex.Message}");
    }
}

public void SaveAlternativeSource(string appId, string? wingetId, string? url, bool priority = false)
{
    try
    {
        if (!alternatives.ContainsKey(appId))
        {
            alternatives[appId] = new AlternativeSource();
        }
        
        if (!string.IsNullOrEmpty(wingetId))
        {
            alternatives[appId].WingetId = wingetId;
            alternatives[appId].Priority = priority;
            
            // НЕМЕДЛЕННО применяем к приложению
            var app = apps.FirstOrDefault(a => a.Id == appId);
            if (app != null)
            {
                app.AlternativeId = wingetId;
                Console.WriteLine($"Applied alternative {wingetId} to {app.DisplayName} immediately");
            }
        }
        
        if (!string.IsNullOrEmpty(url))
        {
            alternatives[appId].Url = url;
            alternatives[appId].UrlPriority = priority;
            
            var app = apps.FirstOrDefault(a => a.Id == appId);
            if (app != null && !app.InstallerUrls.Contains(url))
            {
                if (priority)
                    app.InstallerUrls.Insert(0, url);
                else
                    app.InstallerUrls.Add(url);
            }
        }
        
        alternatives[appId].LastUpdated = DateTime.Now;
        SaveAlternatives();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error saving alternative: {ex.Message}");
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
                    
                File.WriteAllText(alternativesPath, 
                    JsonSerializer.Serialize(alternatives, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
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

        private List<AppInfo> GetDefaultApps()
        {
            return new List<AppInfo>
            {
                // Браузеры
                new AppInfo { Id = "Google.Chrome", DisplayName = "Google Chrome", Category = AppCategory.Браузеры, 
                    InstallerUrls = new List<string> { "https://dl.google.com/chrome/install/latest/chrome_installer.exe" },
                    SilentArgs = "/silent /install", RequiredSpaceMB = 800 },
                new AppInfo { Id = "Mozilla.Firefox", DisplayName = "Mozilla Firefox", Category = AppCategory.Браузеры,
                    InstallerUrls = new List<string> { "https://download.mozilla.org/?product=firefox-latest&os=win64&lang=ru" },
                    SilentArgs = "-ms", RequiredSpaceMB = 600 },
                new AppInfo { Id = "Opera.Opera", DisplayName = "Opera", Category = AppCategory.Браузеры,
                    InstallerUrls = new List<string> { "https://net.geo.opera.com/opera/stable/windows" },
                    SilentArgs = "/silent", RequiredSpaceMB = 500 },
                new AppInfo { Id = "Brave.Brave", DisplayName = "Brave", Category = AppCategory.Браузеры,
                    InstallerUrls = new List<string> { "https://laptop-updates.brave.com/latest/winx64" },
                    SilentArgs = "/S", RequiredSpaceMB = 700 },
                new AppInfo { Id = "Yandex.Browser", DisplayName = "Яндекс.Браузер", Category = AppCategory.Браузеры,
                    InstallerUrls = new List<string> { "https://browser.yandex.ru/download/?os=win&bitness=64" },
                    SilentArgs = "/quiet /install", RequiredSpaceMB = 600 },

                // Офис
                new AppInfo { Id = "LibreOffice.LibreOffice", DisplayName = "LibreOffice", Category = AppCategory.Офис,
                    InstallerUrls = new List<string> { "https://download.documentfoundation.org/libreoffice/stable/24.2.4/win/x86_64/LibreOffice_24.2.4_Win_x64.msi" },
                    SilentArgs = "/qn /norestart", RequiredSpaceMB = 1500 },
                new AppInfo { Id = "Foxit.FoxitReader", DisplayName = "Foxit Reader", Category = AppCategory.Офис,
                    InstallerUrls = new List<string> { "https://cdn01.foxitsoftware.com/pub/foxit/reader/desktop/win/2024.1/enu/FoxitPDFReader20241_enu_Setup.exe" },
                    SilentArgs = "/VERYSILENT", RequiredSpaceMB = 400 },
                new AppInfo { Id = "OnlyOffice.OnlyOffice", DisplayName = "OnlyOffice", Category = AppCategory.Офис,
                    InstallerUrls = new List<string> { "https://github.com/ONLYOFFICE/DesktopEditors/releases/latest/download/DesktopEditors_x64.exe" },
                    SilentArgs = "/S", RequiredSpaceMB = 800 },
                new AppInfo { Id = "OpenOffice.OpenOffice", DisplayName = "Apache OpenOffice", Category = AppCategory.Офис,
                    InstallerUrls = new List<string> { "https://download.archive.apache.org/openoffice/4.1.14/binaries/ru/Apache_OpenOffice_4.1.14_Win_x64_install_ru.exe" },
                    SilentArgs = "/S", RequiredSpaceMB = 600 },
                new AppInfo { Id = "WPS.Office", DisplayName = "WPS Office", Category = AppCategory.Офис,
                    InstallerUrls = new List<string> { "https://wdl1.pcfg.cache.wpscdn.com/wpsdl/WinWPSPro/2023/22.12.1.0/WPSOffice_22.12.1.0.exe" },
                    SilentArgs = "/S", RequiredSpaceMB = 700 },

                // Графика
                new AppInfo { Id = "GIMP.GIMP", DisplayName = "GIMP", Category = AppCategory.Графика,
                    InstallerUrls = new List<string> { "https://download.gimp.org/mirror/pub/gimp/v2.10/windows/gimp-2.10.38-setup.exe" },
                    SilentArgs = "/VERYSILENT", RequiredSpaceMB = 600 },
                new AppInfo { Id = "Inkscape.Inkscape", DisplayName = "Inkscape", Category = AppCategory.Графика,
                    InstallerUrls = new List<string> { "https://inkscape.org/release/inkscape-1.3.2/windows/64-bit/msi/dl/" },
                    SilentArgs = "/qn", RequiredSpaceMB = 500 },
                new AppInfo { Id = "Blender.Blender", DisplayName = "Blender", Category = AppCategory.Графика,
                    InstallerUrls = new List<string> { "https://download.blender.org/release/Blender4.0/blender-4.0.2-windows-x64.msi" },
                    SilentArgs = "/qn /norestart", RequiredSpaceMB = 800 },
                new AppInfo { Id = "Krita.Krita", DisplayName = "Krita", Category = AppCategory.Графика,
                    InstallerUrls = new List<string> { "https://download.kde.org/stable/krita/5.2.2/krita-x64-5.2.2-setup.exe" },
                    SilentArgs = "/S", RequiredSpaceMB = 700 },
                new AppInfo { Id = "Paint.NET", DisplayName = "Paint.NET", Category = AppCategory.Графика,
                    InstallerUrls = new List<string> { "https://github.com/paintdotnet/release/releases/latest/download/paint.net.x64.install.exe" },
                    SilentArgs = "/auto", RequiredSpaceMB = 300 },

                // Разработка
                new AppInfo { Id = "Microsoft.VisualStudioCode", DisplayName = "VS Code", Category = AppCategory.Разработка,
                    InstallerUrls = new List<string> { "https://update.code.visualstudio.com/latest/win32-x64/stable" },
                    SilentArgs = "/verysilent /suppressmsgboxes /mergetasks=!runcode", RequiredSpaceMB = 500 },
                new AppInfo { Id = "Git.Git", DisplayName = "Git", Category = AppCategory.Разработка,
                    InstallerUrls = new List<string> { "https://github.com/git-for-windows/git/releases/latest/download/Git-64-bit.exe" },
                    SilentArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", RequiredSpaceMB = 400 },
                new AppInfo { Id = "Python.Python", DisplayName = "Python 3.12", Category = AppCategory.Разработка,
                    InstallerUrls = new List<string> { "https://www.python.org/ftp/python/3.12.4/python-3.12.4-amd64.exe" },
                    SilentArgs = "/quiet InstallAllUsers=1 PrependPath=1", RequiredSpaceMB = 300 },
                new AppInfo { Id = "Node.js.Node", DisplayName = "Node.js", Category = AppCategory.Разработка,
                    InstallerUrls = new List<string> { "https://nodejs.org/dist/v20.11.0/node-v20.11.0-x64.msi" },
                    SilentArgs = "/qn /norestart", RequiredSpaceMB = 200 },
                new AppInfo { Id = "Microsoft.DotNet.SDK", DisplayName = ".NET SDK 8.0", Category = AppCategory.Разработка,
                    InstallerUrls = new List<string> { "https://builds.dotnet.microsoft.com/dotnet/Sdk/8.0.302/dotnet-sdk-8.0.302-win-x64.exe" },
                    SilentArgs = "/install /quiet /norestart", RequiredSpaceMB = 800 },
                new AppInfo { Id = "Docker.DockerDesktop", DisplayName = "Docker Desktop", Category = AppCategory.Разработка,
                    InstallerUrls = new List<string> { "https://desktop.docker.com/win/main/amd64/Docker%20Desktop%20Installer.exe" },
                    SilentArgs = "install --quiet", RequiredSpaceMB = 2000 },

                // Мессенджеры
                new AppInfo { Id = "Discord.Discord", DisplayName = "Discord", Category = AppCategory.Мессенджеры,
                    InstallerUrls = new List<string> { "https://discord.com/api/download?platform=win" },
                    SilentArgs = "/s", RequiredSpaceMB = 400 },
                new AppInfo { Id = "Telegram.TelegramDesktop", DisplayName = "Telegram", Category = AppCategory.Мессенджеры,
                    InstallerUrls = new List<string> { "https://telegram.org/dl/desktop/win64" },
                    SilentArgs = "/VERYSILENT", RequiredSpaceMB = 300 },
                new AppInfo { Id = "WhatsApp.WhatsApp", DisplayName = "WhatsApp", Category = AppCategory.Мессенджеры,
                    InstallerUrls = new List<string> { "https://web.whatsapp.com/desktop/windows/release/x64/WhatsAppSetup.exe" },
                    SilentArgs = "/S", RequiredSpaceMB = 350 },
                new AppInfo { Id = "Slack.Slack", DisplayName = "Slack", Category = AppCategory.Мессенджеры,
                    InstallerUrls = new List<string> { "https://slack.com/ssb/download-win64" },
                    SilentArgs = "/S", RequiredSpaceMB = 500 },
                new AppInfo { Id = "Zoom.Zoom", DisplayName = "Zoom", Category = AppCategory.Мессенджеры,
                    InstallerUrls = new List<string> { "https://zoom.us/client/latest/ZoomInstaller.exe" },
                    SilentArgs = "/quiet", RequiredSpaceMB = 300 },

                // Мультимедиа
                new AppInfo { Id = "VideoLAN.VLC", DisplayName = "VLC Media Player", Category = AppCategory.Мультимедиа,
                    InstallerUrls = new List<string> { "https://get.videolan.org/vlc/latest/win64/vlc-win64-latest.exe" },
                    SilentArgs = "/S", RequiredSpaceMB = 400 },
                new AppInfo { Id = "Spotify.Spotify", DisplayName = "Spotify", Category = AppCategory.Мультимедиа,
                    InstallerUrls = new List<string> { "https://download.scdn.co/SpotifySetup.exe" },
                    SilentArgs = "/silent", RequiredSpaceMB = 300 },
                new AppInfo { Id = "OBSProject.OBSStudio", DisplayName = "OBS Studio", Category = AppCategory.Мультимедиа,
                    InstallerUrls = new List<string> { "https://cdn-fastly.obsproject.com/downloads/OBS-Studio-30.1.2-Windows-Installer.exe" },
                    SilentArgs = "/S", RequiredSpaceMB = 600 },
                new AppInfo { Id = "Audacity.Audacity", DisplayName = "Audacity", Category = AppCategory.Мультимедиа,
                    InstallerUrls = new List<string> { "https://github.com/audacity/audacity/releases/latest/download/audacity-win-3.4.2-x64.exe" },
                    SilentArgs = "/VERYSILENT", RequiredSpaceMB = 200 },
                new AppInfo { Id = "Kodi.Kodi", DisplayName = "Kodi", Category = AppCategory.Мультимедиа,
                    InstallerUrls = new List<string> { "https://mirrors.kodi.tv/releases/windows/win64/kodi-20.3-Nexus-x64.exe" },
                    SilentArgs = "/S", RequiredSpaceMB = 500 },

                // Системные
                new AppInfo { Id = "7zip.7zip", DisplayName = "7-Zip", Category = AppCategory.Системные,
                    InstallerUrls = new List<string> { "https://www.7-zip.org/a/7z2409-x64.exe" },
                    SilentArgs = "/S", RequiredSpaceMB = 10 },
                new AppInfo { Id = "Notepad++.Notepad++", DisplayName = "Notepad++", Category = AppCategory.Системные,
                    InstallerUrls = new List<string> { "https://github.com/notepad-plus-plus/notepad-plus-plus/releases/latest/download/npp.Installer.x64.exe" },
                    SilentArgs = "/S", RequiredSpaceMB = 20 },
                new AppInfo { Id = "AutoHotkey.AutoHotkey", DisplayName = "AutoHotkey", Category = AppCategory.Системные,
                    InstallerUrls = new List<string> { "https://www.autohotkey.com/download/ahk-install.exe" },
                    SilentArgs = "/S", RequiredSpaceMB = 15 },
                new AppInfo { Id = "Greenshot.Greenshot", DisplayName = "Greenshot", Category = AppCategory.Системные,
                    InstallerUrls = new List<string> { "https://github.com/greenshot/greenshot/releases/download/Greenshot-RELEASE-1.2.10.6/Greenshot-INSTALLER-1.2.10.6-RELEASE.exe" },
                    SilentArgs = "/VERYSILENT", RequiredSpaceMB = 30 },
                new AppInfo { Id = "Ccleaner.Ccleaner", DisplayName = "CCleaner", Category = AppCategory.Системные,
                    InstallerUrls = new List<string> { "https://download.ccleaner.com/ccsetup614.exe" },
                    SilentArgs = "/S", RequiredSpaceMB = 100 },

                // Игровые сервисы
                new AppInfo { Id = "Valve.Steam", DisplayName = "Steam", Category = AppCategory.ИгровыеСервисы,
                    InstallerUrls = new List<string> { "https://cdn.cloudflare.steamstatic.com/client/installer/SteamSetup.exe" },
                    SilentArgs = "/S", RequiredSpaceMB = 2000 },
                new AppInfo { Id = "EpicGames.EpicGamesLauncher", DisplayName = "Epic Games Launcher", Category = AppCategory.ИгровыеСервисы,
                    InstallerUrls = new List<string> { "https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/installer/download/EpicGamesLauncherInstaller.msi" },
                    SilentArgs = "/qn /norestart", RequiredSpaceMB = 1500 },
                new AppInfo { Id = "Battle.net", DisplayName = "Battle.net", Category = AppCategory.ИгровыеСервисы,
                    InstallerUrls = new List<string> { "https://www.blizzard.com/download/get/Battle.net-Setup.exe" },
                    SilentArgs = "/S", RequiredSpaceMB = 600 },
                new AppInfo { Id = "Discord.Gaming", DisplayName = "Discord (игровой)", Category = AppCategory.ИгровыеСервисы,
                    InstallerUrls = new List<string> { "https://discord.com/api/download?platform=win" },
                    SilentArgs = "/s", RequiredSpaceMB = 400 },
                new AppInfo { Id = "NVIDIA.GeForceExperience", DisplayName = "NVIDIA GeForce Experience", Category = AppCategory.ИгровыеСервисы,
                    InstallerUrls = new List<string> { "https://us.download.nvidia.com/GFE/GFEClient/3.27.0.120/GeForce_Experience_v3.27.0.120.exe" },
                    SilentArgs = "/s /noreboot", RequiredSpaceMB = 500 },



                // Драйверпаки
                new AppInfo { Id = "Snappy.DriverInstaller", DisplayName = "Snappy Driver Installer", Category = AppCategory.Драйверпаки,
                    InstallerUrls = new List<string> { "https://sdi-tool.org/download/SDIO_2025.exe" },
                    SilentArgs = "/VERYSILENT", RequiredSpaceMB = 1500 },
                
                new AppInfo { Id = "AMD.AutoDetect", DisplayName = "AMD Auto Detect", Category = AppCategory.Драйверпаки,
                    InstallerUrls = new List<string> { "https://www.amd.com/system/files/AMD-Auto-Detect-Setup.exe" },
                    SilentArgs = "/S", RequiredSpaceMB = 300 },
                
                // 👇 Driver Booster Pro (ВСТАВЛЕНО ПРАВИЛЬНО, С ЗАПЯТОЙ ПОСЛЕ AMD)
                new AppInfo
                {
                    Id = "IObit.DriverBoosterPro",
                    DisplayName = "Driver Booster Pro",
                    Category = AppCategory.Драйверпаки,
                    InstallerUrls = new List<string>
                    {
                        @"https://downloader.disk.yandex.ru/disk/58d017358f8a833b138299d362fab36f08541232b24be1367d74a9dc91e31c20/69bb2388/xxAINjah5GhdzpOTkvitMdAGhZuRiVZ6Dc4kFewUuKIewXnjAsW8mBGwj4hTIT3oXxHcyDeQDzkdwQo_L-yx-g%3D%3D?uid=0&filename=Driver.Booster.Pro-13.2.0.184.exe&disposition=attachment&hash=CEfIHNpNtDD6W5NLXpkbIHA%2By7BEqC5qWoEM8XlrKLSXWd4UjtBFtzA24NaQyQVNq/J6bpmRyOJonT3VoXnDag%3D%3D&limit=0&content_type=application%2Fvnd.microsoft.portable-executable&owner_uid=269218471&fsize=40879294&hid=3040bacb8caa2489aab3bd53cabbec97&media_type=executable&tknv=v3"
                    },
                    SilentArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                    RequiredSpaceMB = 200
                }
            }; // <- Закрывающая скобка списка
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