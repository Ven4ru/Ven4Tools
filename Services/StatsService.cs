using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public class StatsService
    {
        private readonly string _statsPath;
        private readonly ConsentService _consentService;
        private static readonly SemaphoreSlim _lock = new(1, 1);

        private static StatsService? _instance;
        public static StatsService Instance => _instance ??= new StatsService();

        private StatsService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var ven4Folder = Path.Combine(appData, "Ven4Tools");
            if (!Directory.Exists(ven4Folder))
                Directory.CreateDirectory(ven4Folder);

            _statsPath = Path.Combine(ven4Folder, "stats.json");
            _consentService = new ConsentService();
        }

        public async Task<bool> IsStatsAllowedAsync() => await _consentService.IsStatsAllowedAsync();

        public async Task TrackUserAddAsync(string appId, string? wingetId = null, string? url = null)
        {
            if (!await IsStatsAllowedAsync()) return;

            await _lock.WaitAsync();
            try
            {
                var stats = await LoadStatsAsync();

                if (!stats.UserAdds.ContainsKey(appId))
                    stats.UserAdds[appId] = new AppStats();

                var appStats = stats.UserAdds[appId];
                appStats.Count++;

                if (!string.IsNullOrEmpty(wingetId) && !appStats.WingetIds.Contains(wingetId))
                    appStats.WingetIds.Add(wingetId);

                if (!string.IsNullOrEmpty(url) && !appStats.Urls.Contains(url))
                    appStats.Urls.Add(url);

                await SaveStatsAsync(stats);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task TrackOverrideAsync(string appId, string? wingetId, string? url, bool success = false)
        {
            if (!await IsStatsAllowedAsync()) return;

            await _lock.WaitAsync();
            try
            {
                var stats = await LoadStatsAsync();

                if (!stats.Overrides.ContainsKey(appId))
                    stats.Overrides[appId] = 0;
                stats.Overrides[appId]++;

                if (!stats.OverrideDetails.ContainsKey(appId))
                    stats.OverrideDetails[appId] = new OverrideDetails();

                var details = stats.OverrideDetails[appId];
                details.TotalOverrides++;
                if (success) details.SuccessfulInstalls++;

                if (!string.IsNullOrEmpty(wingetId))
                {
                    var existing = details.WingetSelections.FirstOrDefault(x => x.Id == wingetId);
                    if (existing != null)
                    {
                        existing.Count++;
                        if (success) existing.SuccessCount++;
                    }
                    else
                    {
                        details.WingetSelections.Add(new WingetSelection
                        {
                            Id = wingetId,
                            Count = 1,
                            SuccessCount = success ? 1 : 0
                        });
                        if (details.WingetSelections.Count > 10)
                            details.WingetSelections = details.WingetSelections.OrderByDescending(x => x.Count).Take(10).ToList();
                    }
                }

                if (!string.IsNullOrEmpty(url))
                {
                    var existing = details.UrlSelections.FirstOrDefault(x => x.Url == url);
                    if (existing != null)
                    {
                        existing.Count++;
                        if (success) existing.SuccessCount++;
                    }
                    else
                    {
                        details.UrlSelections.Add(new UrlSelection
                        {
                            Url = url,
                            Count = 1,
                            SuccessCount = success ? 1 : 0
                        });
                        if (details.UrlSelections.Count > 10)
                            details.UrlSelections = details.UrlSelections.OrderByDescending(x => x.Count).Take(10).ToList();
                    }
                }

                await SaveStatsAsync(stats);
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task<Stats> LoadStatsAsync()
        {
            if (!File.Exists(_statsPath))
                return new Stats();

            var json = await File.ReadAllTextAsync(_statsPath);
            return JsonConvert.DeserializeObject<Stats>(json) ?? new Stats();
        }

        private async Task SaveStatsAsync(Stats stats)
        {
            stats.LastUpdate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var json = JsonConvert.SerializeObject(stats, Formatting.Indented);
            await File.WriteAllTextAsync(_statsPath, json);
        }
    }
}