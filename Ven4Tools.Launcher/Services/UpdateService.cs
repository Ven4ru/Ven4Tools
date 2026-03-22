using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services;

public class UpdateService : IDisposable
{
    private readonly GitHubService _gitHubService;
    
    public UpdateService()
    {
        _gitHubService = new GitHubService("Ven4ru", "Ven4Tools");
    }
    
    public string GetCurrentVersion(string? appPath)
    {
        if (string.IsNullOrEmpty(appPath) || !File.Exists(appPath))
            return "0.0.0.0";
            
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(appPath);
            return versionInfo.FileVersion ?? "0.0.0.0";
        }
        catch
        {
            return "0.0.0.0";
        }
    }
    
    public async Task<UpdateInfo> CheckForUpdateAsync(string? appPath)
{
    var result = new UpdateInfo
    {
        IsInstalled = !string.IsNullOrEmpty(appPath) && File.Exists(appPath),
        CurrentVersion = GetCurrentVersion(appPath)
    };
    
    // Добавляем отладку
    Console.WriteLine($"[DEBUG] CheckForUpdateAsync: appPath={appPath ?? "null"}");
    Console.WriteLine($"[DEBUG] IsInstalled={result.IsInstalled}");
    Console.WriteLine($"[DEBUG] CurrentVersion={result.CurrentVersion}");
    
    // Если программа установлена, выводим дополнительную информацию
    if (result.IsInstalled && appPath != null)
    {
        try
        {
            var fileInfo = new FileInfo(appPath);
            Console.WriteLine($"[DEBUG] Program location: {appPath}");
            Console.WriteLine($"[DEBUG] File size: {fileInfo.Length} bytes");
            Console.WriteLine($"[DEBUG] Last modified: {fileInfo.LastWriteTime}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] Error getting file info: {ex.Message}");
        }
    }
    
    try
    {
        var release = await _gitHubService.GetLatestRelease();
        
        if (release == null || string.IsNullOrEmpty(release.tag_name))
        {
            result.Error = "Не удалось получить информацию о версиях";
            return result;
        }
        
        result.LatestVersion = release.tag_name.TrimStart('v');
        Console.WriteLine($"[DEBUG] LatestVersion={result.LatestVersion}");
        
        // Ищем файл установщика
        var installer = release.assets?.FirstOrDefault(a => 
            a.name != null && (a.name.Contains("Setup") || a.name.Contains("Installer") || a.name.EndsWith(".exe")));
        
        if (installer != null)
        {
            result.DownloadUrl = installer.browser_download_url;
            result.FileSize = installer.size;
            Console.WriteLine($"[DEBUG] Found installer: {installer.name}");
            Console.WriteLine($"[DEBUG] DownloadUrl={result.DownloadUrl}");
        }
        else
        {
            var availableFiles = release.assets != null 
                ? string.Join(", ", release.assets.Select(a => a.name)) 
                : "нет файлов";
            result.Error = $"Не найден установщик. Доступные: {availableFiles}";
            Console.WriteLine($"[DEBUG] {result.Error}");
        }
        
        result.ReleaseNotes = release.body;
        
        // Сравниваем версии
        if (result.CurrentVersion != "0.0.0.0" && result.LatestVersion != null)
        {
            try
            {
                var current = new Version(result.CurrentVersion);
                var latest = new Version(result.LatestVersion);
                result.HasUpdate = latest > current;
                Console.WriteLine($"[DEBUG] Current={current}, Latest={latest}, HasUpdate={result.HasUpdate}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG] Version parse error: {ex.Message}");
                result.HasUpdate = false;
            }
        }
        else
        {
            Console.WriteLine($"[DEBUG] Skipping version compare (CurrentVersion={result.CurrentVersion})");
            result.HasUpdate = false;
        }
    }
    catch (Exception ex)
    {
        result.Error = ex.Message;
        Console.WriteLine($"[DEBUG] Exception: {ex.Message}");
    }
    
    return result;
}

    public async Task<bool> DownloadAndInstallAsync(string downloadUrl, string destinationPath, IProgress<double>? progress = null)
    {
        try
        {
            Console.WriteLine($"[DEBUG] DownloadAndInstallAsync: url={downloadUrl}");
            Console.WriteLine($"[DEBUG] Destination: {destinationPath}");
            
            var success = await _gitHubService.DownloadFile(downloadUrl, destinationPath, progress);
            
            if (!success)
            {
                Console.WriteLine("[DEBUG] Download failed");
                return false;
            }
            
            // Проверяем, что файл действительно скачался
            var fileInfo = new FileInfo(destinationPath);
            Console.WriteLine($"[DEBUG] File downloaded, size: {fileInfo.Length} bytes");
            
            if (fileInfo.Length == 0)
            {
                Console.WriteLine("[DEBUG] File is empty!");
                return false;
            }
            
            Console.WriteLine("[DEBUG] Launching installer...");
            
            var startInfo = new ProcessStartInfo
            {
                FileName = destinationPath,
                Arguments = "/SILENT /VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = true
            };
            
            var process = Process.Start(startInfo);
            if (process != null)
            {
                Console.WriteLine($"[DEBUG] Installer started (PID: {process.Id})");
                await Task.Delay(2000);
                return true;
            }
            
            Console.WriteLine("[DEBUG] Failed to start installer");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] Exception: {ex.Message}");
            return false;
        }
    }
    
    public void Dispose()
    {
        _gitHubService?.Dispose();
    }
}