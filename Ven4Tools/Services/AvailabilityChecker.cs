using System;
using System.Collections.Concurrent;
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
        private readonly ConcurrentDictionary<string, CachedAvailability> cache = new();
        private readonly TimeSpan cacheDuration = TimeSpan.FromMinutes(5);

        public AvailabilityChecker()
        {
            httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools");
            httpClient.Timeout = TimeSpan.FromSeconds(AppSettings.CheckTimeout);
        }

        public void UpdateTimeout(int seconds)
        {
            httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(5, seconds));
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

            // Cache by the resolved ID so AlternativeId changes invalidate correctly
            string cacheKey = wingetId;

            if (cache.TryGetValue(cacheKey, out var cached) && DateTime.Now - cached.Timestamp < cacheDuration)
                return (cached.Status, cached.SizeMB);

            (AvailabilityStatus Status, long SizeMB) result = (AvailabilityStatus.Unavailable, 0);

            if (!string.IsNullOrEmpty(wingetId) && !wingetId.StartsWith("User."))
                result = await GetWingetPackageInfo(wingetId);

            if (result.Status != AvailabilityStatus.Available && app.InstallerUrls != null && app.InstallerUrls.Count > 0)
            {
                foreach (var url in app.InstallerUrls)
                {
                    var urlResult = await GetUrlInfo(url);
                    if (urlResult.Status == AvailabilityStatus.Available)
                    {
                        result = urlResult;
                        break;
                    }
                }
            }

            CacheResult(cacheKey, new AppAvailabilityResult { Status = result.Status, SizeMB = result.SizeMB });
            return result;
        }

        private void CacheResult(string appId, AppAvailabilityResult result)
        {
            var entry = new CachedAvailability
            {
                Status = result.Status,
                SizeMB = result.SizeMB,
                Timestamp = DateTime.Now
            };
            cache.AddOrUpdate(appId, entry, (_, __) => entry);
        }

        private async Task<(AvailabilityStatus Status, long SizeMB)> GetWingetPackageInfo(string appId)
        {
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
                    return (AvailabilityStatus.Unavailable, 0);

                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                bool success = process.ExitCode == 0 &&
                               (output.Contains("Version", StringComparison.OrdinalIgnoreCase) ||
                                output.Contains("Found", StringComparison.OrdinalIgnoreCase));

                if (success)
                {
                    long size = ParseWingetSize(output);
                    return (AvailabilityStatus.Available, size > 0 ? size : 120);
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

                            return unit switch
                            {
                                "KB" => (long)(value / 1024),
                                "MB" => (long)value,
                                "GB" => (long)(value * 1024),
                                _ => (long)value
                            };
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
                            size = response.Content.Headers.ContentLength.Value / 1024 / 1024;
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
                                        size = getResponse.Content.Headers.ContentLength.Value / 1024 / 1024;
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

        public void ClearCache() => cache.Clear();

        public void Dispose()
        {
            httpClient?.Dispose();
        }
    }
}
