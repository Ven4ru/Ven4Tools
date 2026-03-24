using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public class AvailabilityChecker : IDisposable
    {
        private readonly HttpClient httpClient;
        private readonly Dictionary<string, CachedAvailability> cache = new();
        private readonly TimeSpan cacheDuration = TimeSpan.FromMinutes(5);
        
        public AvailabilityChecker()
        {
            httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools");
            httpClient.Timeout = TimeSpan.FromSeconds(15);
        }

        private class CachedAvailability
        {
            public AvailabilityStatus Status { get; set; }
            public long SizeMB { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public enum AvailabilityStatus
        {
            Unknown,
            Available,
            Unavailable
        }

        public class AppAvailabilityResult
        {
            public AvailabilityStatus Status { get; set; }
            public long SizeMB { get; set; }
            public string? Source { get; set; }
        }

public async Task<(AvailabilityStatus Status, long SizeMB)> CheckAppAvailabilityWithSize(AppInfo app)
{
    string wingetId = !string.IsNullOrEmpty(app.AlternativeId) 
        ? app.AlternativeId 
        : app.Id ?? string.Empty;

    Debug.WriteLine($"[Availability] Проверка {app.DisplayName ?? app.Id} → Winget ID: {wingetId}");

    // Если есть прямые ссылки — можно проверять их в первую очередь (по желанию)
    if (app.InstallerUrls != null && app.InstallerUrls.Count > 0)
    {
        // твой старый код проверки по URL (HEAD) — оставь, если он есть
    }

    // Для приложений без ссылок или с AlternativeId — используем winget
    var result = await GetWingetPackageInfo(wingetId);

    Debug.WriteLine($"[Availability] Результат для {app.DisplayName ?? app.Id}: {(result.Status == AvailabilityStatus.Available ? "✅" : "❌")} (~{result.SizeMB} МБ)");

    return result;
}

        private void CacheResult(string appId, AppAvailabilityResult result)
        {
            cache[appId] = new CachedAvailability
            {
                Status = result.Status,
                SizeMB = result.SizeMB,
                Timestamp = DateTime.Now
            };
        }

private async Task<(AvailabilityStatus Status, long SizeMB)> GetWingetPackageInfo(string appId)
{
    Debug.WriteLine("=== WINGET CHECK START === ID: '" + appId + "' ===");

    try
    {
        string args = $"show --id \"{appId}\" --exact --source winget --accept-source-agreements";

        var psi = new ProcessStartInfo
        {
            FileName = "winget.exe",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            Debug.WriteLine("!!! НЕ УДАЛОСЬ ЗАПУСТИТЬ WINGET !!!");
            return (AvailabilityStatus.Unavailable, 0);
        }

        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Debug.WriteLine($"ExitCode = {process.ExitCode} | Output length = {output.Length}");

        bool success = process.ExitCode == 0 &&
                       (output.Contains("Version", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("Found", StringComparison.OrdinalIgnoreCase));

        if (success)
        {
            Debug.WriteLine("!!! WINGET УСПЕШНО НАШЁЛ ПАКЕТ !!! Python.Python.3.14 работает");
            long size = ParseWingetSize(output);
            return (AvailabilityStatus.Available, size > 0 ? size : 120);
        }
        else
        {
            Debug.WriteLine("!!! WINGET НЕ НАШЁЛ !!! Первые 800 символов:");
            Debug.WriteLine(output.Substring(0, Math.Min(800, output.Length)));
        }
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"!!! ИСКЛЮЧЕНИЕ: {ex.Message}");
    }

    Debug.WriteLine("=== WINGET CHECK END (НЕУДАЧА) ===");
    return (AvailabilityStatus.Unavailable, 0);
}

        private long ParseWingetSize(string output)
        {
            try
            {
                var lines = output.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains("Installer Size") || line.Contains("Size"))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+[,.]?\d*)\s*(MB|KB|GB)");
                        if (match.Success)
                        {
                            double value = double.Parse(match.Groups[1].Value.Replace(',', '.'));
                            string unit = match.Groups[2].Value;
                            
                            switch (unit)
                            {
                                case "KB": return (long)(value / 1024);
                                case "MB": return (long)value;
                                case "GB": return (long)(value * 1024);
                                default: return (long)value;
                            }
                        }
                    }
                }
            }
            catch { }
            
            return 100;
        }

        private async Task<(AvailabilityStatus Status, long SizeMB)> GetUrlInfo(string url)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Head, url))
                using (var response = await httpClient.SendAsync(request))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        long size = 0;
                        if (response.Content.Headers.ContentLength.HasValue)
                        {
                            size = response.Content.Headers.ContentLength.Value / 1024 / 1024;
                        }
                        return (AvailabilityStatus.Available, size > 0 ? size : 100);
                    }
                    
                    if (response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
                    {
                        using (var getRequest = new HttpRequestMessage(HttpMethod.Get, url))
                        {
                            getRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                            using (var getResponse = await httpClient.SendAsync(getRequest))
                            {
                                if (getResponse.IsSuccessStatusCode || getResponse.StatusCode == System.Net.HttpStatusCode.PartialContent)
                                {
                                    long size = 0;
                                    if (getResponse.Content.Headers.ContentLength.HasValue)
                                    {
                                        size = getResponse.Content.Headers.ContentLength.Value / 1024 / 1024;
                                    }
                                    return (AvailabilityStatus.Available, size > 0 ? size : 100);
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            
            return (AvailabilityStatus.Unavailable, 0);
        }

        public void Dispose()
        {
            httpClient?.Dispose();
        }
    }
}
