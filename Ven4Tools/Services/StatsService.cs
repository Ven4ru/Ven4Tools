using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public class StatsService
    {
        private readonly string _statsPath;
        private readonly ConsentService _consentService;
        
        public StatsService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var ven4Folder = Path.Combine(appData, "Ven4Tools");
            if (!Directory.Exists(ven4Folder))
                Directory.CreateDirectory(ven4Folder);
            
            _statsPath = Path.Combine(ven4Folder, "stats.json");
            _consentService = new ConsentService();
        }
        
        public async Task TrackUserAddAsync(string appId, string? wingetId = null, string? url = null)
        {
            // Проверяем согласие
            var allowStats = await _consentService.IsStatsAllowedAsync();
            if (!allowStats) return;
            
            var stats = await LoadStatsAsync();
            
            if (!stats.UserAdds.ContainsKey(appId))
            {
                stats.UserAdds[appId] = new AppStats();
            }
            
            stats.UserAdds[appId].Count++;
            
            if (!string.IsNullOrEmpty(wingetId) && !stats.UserAdds[appId].WingetIds.Contains(wingetId))
            {
                stats.UserAdds[appId].WingetIds.Add(wingetId);
            }
            
            if (!string.IsNullOrEmpty(url) && !stats.UserAdds[appId].Urls.Contains(url))
            {
                stats.UserAdds[appId].Urls.Add(url);
            }
            
            await SaveStatsAsync(stats);
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
