using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services
{
    public class GitHubService : IDisposable
    {
        private readonly HttpClient httpClient;
        private readonly string repoOwner;
        private readonly string repoName;

        // Конструктор по умолчанию для Ven4Tools
        public GitHubService() : this("Ven4ru", "Ven4Tools")
        {
        }

        public GitHubService(string repoOwner, string repoName)
        {
            this.repoOwner = repoOwner;
            this.repoName = repoName;
            
            httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools.Launcher");
            httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
            httpClient.Timeout = TimeSpan.FromSeconds(15);
        }

        public async Task<GitHubRelease?> GetLatestRelease()
        {
            try
            {
                string url = $"https://api.github.com/repos/{repoOwner}/{repoName}/releases/latest";
                using var response = await httpClient.GetAsync(url);
                
                if (!response.IsSuccessStatusCode)
                    return null;
                
                string json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<GitHubRelease>(json);
            }
            catch
            {
                return null;
            }
        }

public async Task<bool> DownloadFile(string url, string destPath, IProgress<double>? progress = null)
{
    try
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        
        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        var bytesDownloaded = 0L;
        
        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new System.IO.FileStream(destPath, System.IO.FileMode.Create);
        
        var buffer = new byte[81920];
        int bytesRead;
        
        while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            bytesDownloaded += bytesRead;
            
            if (totalBytes > 0 && progress != null)
            {
                var percent = (double)bytesDownloaded / totalBytes;
                progress.Report(percent);
                
                // Отладка в консоль (не в UI)
                Console.WriteLine($"[DEBUG] Progress: {(int)(percent * 100)}%");
            }
        }
        
        // Принудительно отправляем 100%
        progress?.Report(1.0);
        
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[DEBUG] Download error: {ex.Message}");
        return false;
    }
}

        public void Dispose()
        {
            httpClient?.Dispose();
        }
    }
}