using System;
using System.Net.Http;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Text.Json;

namespace Ven4Tools.Services
{
    public class YandexDownloadInfo
    {
        public string? Href { get; set; }
        public string? Method { get; set; }
        public bool Templated { get; set; }
    }

    public class UpdateInfo
    {
        public bool HasUpdate { get; set; }
        public string? LatestVersion { get; set; }
        public string? DownloadUrl { get; set; }
        public string? ReleaseNotes { get; set; }
        public string? Error { get; set; }
    }

    public class UpdateChecker
    {
        private readonly string publicFolderUrl;
        private readonly HttpClient httpClient;
        private readonly string currentVersion;
        
public UpdateChecker(string yandexPublicFolderUrl)
{
    publicFolderUrl = yandexPublicFolderUrl;
    httpClient = new HttpClient();
    httpClient.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools");
    
    currentVersion = Assembly.GetExecutingAssembly()
        .GetName()
        .Version?
        .ToString() ?? "1.0.0";
    
    // ПРОСТЕЙШАЯ ПРОВЕРКА
    try
    {
        File.WriteAllText(@"C:\Users\Ven4\debug_simple.log", 
            $"{DateTime.Now}: Конструктор сработал!\n");
    }
    catch { }
}
public async Task<UpdateInfo> CheckForUpdateAsync()
{
    // ПРОСТЕЙШАЯ ПРОВЕРКА
    try
    {
        File.AppendAllText(@"C:\Users\Ven4\debug_simple.log", 
            $"{DateTime.Now}: CheckForUpdateAsync вызван!\n");
    }
    catch { }
    
    try
    {
        string? latestVersion = await GetLatestVersionFromDisk();
                
                if (string.IsNullOrEmpty(latestVersion))
                    return new UpdateInfo { HasUpdate = false };
                
                if (currentVersion != latestVersion)
                {
                    string downloadUrl = $"https://cloud-api.yandex.net/v1/disk/public/resources/download?public_key={publicFolderUrl}&path=/Ven4Tools_Setup_{latestVersion}.exe";
                    string changelog = await GetChangelog();
                    
                    return new UpdateInfo
                    {
                        HasUpdate = true,
                        LatestVersion = latestVersion,
                        DownloadUrl = downloadUrl,
                        ReleaseNotes = changelog
                    };
                }
                
                return new UpdateInfo { HasUpdate = false };
            }
            catch (Exception ex)
            {
                return new UpdateInfo { HasUpdate = false, Error = ex.Message };
            }
        }

private async Task<string?> GetLatestVersionFromDisk()
{
    try
    {
        string versionUrl = $"https://cloud-api.yandex.net/v1/disk/public/resources/download?public_key={publicFolderUrl}&path=/version.txt";
        
        using var client = new HttpClient();
        var response = await client.GetAsync(versionUrl);
        response.EnsureSuccessStatusCode();
        
        string json = await response.Content.ReadAsStringAsync();
        
        // Вытаскиваем href вручную через регулярное выражение
        var match = System.Text.RegularExpressions.Regex.Match(json, @"""href"":""([^""]+)""");
        if (!match.Success)
            return null;
        
        string href = match.Groups[1].Value;
        
        // Скачиваем файл
        using var fileResponse = await client.GetAsync(href);
        fileResponse.EnsureSuccessStatusCode();
        
        string content = await fileResponse.Content.ReadAsStringAsync();
        return content.Trim();
    }
    catch
    {
        return null;
    }
}

        public async Task<string> GetChangelog()
        {
            try
            {
                string changelogUrl = $"https://cloud-api.yandex.net/v1/disk/public/resources/download?public_key={publicFolderUrl}&path=/changelog.txt";
                
                using var response = await httpClient.GetAsync(changelogUrl);
                response.EnsureSuccessStatusCode();
                
                var json = await response.Content.ReadAsStringAsync();
                var downloadInfo = JsonSerializer.Deserialize<YandexDownloadInfo>(json);
                
                if (downloadInfo?.Href == null)
                    return string.Empty;
                
                using var fileResponse = await httpClient.GetAsync(downloadInfo.Href);
                fileResponse.EnsureSuccessStatusCode();
                
                return await fileResponse.Content.ReadAsStringAsync();
            }
            catch
            {
                return string.Empty;
            }
        }

        public async Task<bool> DownloadAndRunUpdate(string downloadUrl)
        {
            try
            {
                string tempFile = Path.Combine(Path.GetTempPath(), $"Ven4Tools_Update_{Guid.NewGuid()}.exe");
                
                using var response = await httpClient.GetAsync(downloadUrl);
                response.EnsureSuccessStatusCode();
                
                var json = await response.Content.ReadAsStringAsync();
                var downloadInfo = JsonSerializer.Deserialize<YandexDownloadInfo>(json);
                
                if (downloadInfo?.Href == null)
                    return false;
                
                using var fileResponse = await httpClient.GetAsync(downloadInfo.Href);
                fileResponse.EnsureSuccessStatusCode();
                
                using var fileStream = File.Create(tempFile);
                await fileResponse.Content.CopyToAsync(fileStream);
                
                System.Diagnostics.Process.Start(tempFile);
                
                return true;
            }
            catch
            {
                return false;
            }
        }

        public string GetCurrentVersion() => currentVersion;
    }
}