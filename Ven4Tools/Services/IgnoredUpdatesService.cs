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
        // Файл есть, но не прочитался: запись стёрла бы прежние записи (см. SettingsFileReader).
        private bool _loadFailed;

        public IgnoredUpdatesService() => Load();

        private void Load()
        {
            const string Log = "[IgnoredUpdatesService]";
            var read = SettingsFileReader.TryRead(FilePath, Log, out string json);
            _loadFailed = read == SettingsReadResult.Unreadable;
            if (read != SettingsReadResult.Read) return;
            try
            {
                _ignored = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new();
            }
            catch (JsonException ex)
            {
                _ignored = new();
                _loadFailed = !SettingsFileReader.SetAsideCorrupt(FilePath, Log, ex.Message);
            }
        }

        private void Save()
        {
            if (_loadFailed)
            {
                AppLogger.Write("[IgnoredUpdatesService] Сохранение пропущено: файл не прочитан, запись стёрла бы прежние записи");
                return;
            }
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
