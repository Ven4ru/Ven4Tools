using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Ven4Tools.Helpers;

namespace Ven4Tools.Services
{
    // Локальное хранилище пропущенных обновлений: appId → версия, которую
    // пользователь решил пока не ставить. Если позже выходит версия НОВЕЕ
    // сохранённой — обновление снова показывается автоматически (сравнение
    // строк версий делает CatalogViewModel.Availability.cs, не этот сервис).
    public class IgnoredUpdatesService
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "ignored_updates.json");

        private Dictionary<string, string> _ignored = new();

        public IgnoredUpdatesService() => Load();

        private void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    _ignored = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                        File.ReadAllText(FilePath)) ?? new();
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[IgnoredUpdatesService] Чтение пропущенных обновлений: {ex.Message}");
                _ignored = new();
            }
        }

        private void Save()
        {
            try
            {
                FileHelper.WriteAllTextAtomic(FilePath,
                    JsonConvert.SerializeObject(_ignored, Formatting.Indented));
            }
            catch (Exception ex) { AppLogger.Write($"[IgnoredUpdatesService] Save: {ex.Message}"); }
        }

        public string? GetIgnoredVersion(string appId) =>
            _ignored.TryGetValue(appId, out var v) ? v : null;

        public void Ignore(string appId, string version)
        {
            _ignored[appId] = version;
            Save();
        }

        public void ClearIgnore(string appId)
        {
            if (_ignored.Remove(appId)) Save();
        }
    }
}
