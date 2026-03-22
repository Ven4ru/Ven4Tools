using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public class AvailabilityChecker
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

        // Старый метод для обратной совместимости
        public async Task<AvailabilityStatus> CheckAppAvailability(AppInfo app)
        {
            var result = await CheckAppAvailabilityWithSize(app);
            return result.Status;
        }

        public async Task<AppAvailabilityResult> CheckAppAvailabilityWithSize(AppInfo app)
        {
            var result = new AppAvailabilityResult { Status = AvailabilityStatus.Unavailable, SizeMB = 0 };

            // Проверяем кеш
            if (cache.TryGetValue(app.Id, out var cached) && 
                DateTime.Now - cached.Timestamp < cacheDuration)
            {
                result.Status = cached.Status;
                result.SizeMB = cached.SizeMB;
                return result;
            }

            // 1. Проверяем альтернативный ID
            if (!string.IsNullOrEmpty(app.AlternativeId))
            {
                var (status, size) = await GetWingetPackageInfo(app.AlternativeId);
                if (status == AvailabilityStatus.Available)
                {
                    result.Status = status;
                    result.SizeMB = size;
                    result.Source = $"winget:{app.AlternativeId}";
                    CacheResult(app.Id, result);
                    return result;
                }
            }
            
            // 2. Проверяем оригинальный ID
            if (result.Status != AvailabilityStatus.Available && 
                !string.IsNullOrEmpty(app.Id) && !app.Id.StartsWith("User."))
            {
                var (status, size) = await GetWingetPackageInfo(app.Id);
                if (status == AvailabilityStatus.Available)
                {
                    result.Status = status;
                    result.SizeMB = size;
                    result.Source = $"winget:{app.Id}";
                    CacheResult(app.Id, result);
                    return result;
                }
            }

            // 3. Проверяем прямые ссылки
            if (app.InstallerUrls.Any())
            {
                foreach (var url in app.InstallerUrls)
                {
                    var (status, size) = await GetUrlInfo(url);
                    if (status == AvailabilityStatus.Available)
                    {
                        result.Status = status;
                        result.SizeMB = size;
                        result.Source = url;
                        CacheResult(app.Id, result);
                        return result;
                    }
                }
            }

            CacheResult(app.Id, result);
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
            try
            {
                string args = $"show --id {appId} --exact --source winget --accept-source-agreements";
                
                var psi = new ProcessStartInfo
                {
                    FileName = "winget.exe",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null) return (AvailabilityStatus.Unavailable, 0);
                    
                    string output = await process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();

                    if (process.ExitCode == 0)
                    {
                        long size = ParseWingetSize(output);
                        return (AvailabilityStatus.Available, size > 0 ? size : 100);
                    }
                }
            }
            catch { }

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
    }
}