using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Ven4Tools.Helpers;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public class InstallHistoryService
    {
        private static readonly Lazy<InstallHistoryService> _lazy =
            new(() => new InstallHistoryService());
        public static InstallHistoryService Instance => _lazy.Value;

        private readonly string _path;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private const int MaxEntries = 100;

        public event Action? Changed;

        private InstallHistoryService()
        {
            _path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ven4Tools", "install_history.json");
        }

        public async Task TrackAsync(string appId, string appName, string source,
            string category = "", bool success = true)
        {
            await _lock.WaitAsync();
            try
            {
                var list = await LoadAsync();
                list.Insert(0, new HistoryEntry
                {
                    AppId       = appId,
                    AppName     = appName,
                    Source      = source,
                    Category    = category,
                    MachineName = Environment.MachineName,
                    InstalledAt = DateTime.Now,
                    Success     = success
                });
                if (list.Count > MaxEntries)
                    list.RemoveRange(MaxEntries, list.Count - MaxEntries);
                await SaveAsync(list);
                Changed?.Invoke();
            }
            finally { _lock.Release(); }
        }

        public async Task<List<HistoryEntry>> GetHistoryAsync()
        {
            await _lock.WaitAsync();
            try { return await LoadAsync(); }
            finally { _lock.Release(); }
        }

        public async Task ClearAsync()
        {
            await _lock.WaitAsync();
            try
            {
                await SaveAsync(new List<HistoryEntry>());
                Changed?.Invoke();
            }
            finally { _lock.Release(); }
        }

        private async Task<List<HistoryEntry>> LoadAsync()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = await File.ReadAllTextAsync(_path);
                    return JsonConvert.DeserializeObject<List<HistoryEntry>>(json)
                           ?? new List<HistoryEntry>();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "Ошибка загрузки истории установок");
            }
            return new List<HistoryEntry>();
        }

        private async Task SaveAsync(List<HistoryEntry> list)
        {
            try
            {
                await FileHelper.WriteAllTextAtomicAsync(_path,
                    JsonConvert.SerializeObject(list, Formatting.Indented));
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "Ошибка сохранения истории установок");
            }
        }
    }
}
