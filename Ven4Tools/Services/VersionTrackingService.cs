using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Ven4Tools.Helpers;

namespace Ven4Tools.Services
{
    public class VersionTrackingService
    {
        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "version_tracking.json");

        private Dictionary<string, TrackedInstall> _data = new();
        // Файл есть, но не прочитался: запись стёрла бы прежние записи (см. SettingsFileReader).
        private bool _loadFailed;

        public VersionTrackingService() => Load();

        private void Load()
        {
            const string Log = "[VersionTrackingService]";
            var read = SettingsFileReader.TryRead(_path, Log, out string json);
            _loadFailed = read == SettingsReadResult.Unreadable;
            if (read != SettingsReadResult.Read) return;
            try
            {
                _data = JsonConvert.DeserializeObject<Dictionary<string, TrackedInstall>>(json) ?? new();
            }
            catch (JsonException ex)
            {
                _data = new();
                _loadFailed = !SettingsFileReader.SetAsideCorrupt(_path, Log, ex.Message);
            }
        }

        private void Save()
        {
            if (_loadFailed)
            {
                AppLogger.Write("[VersionTrackingService] Сохранение пропущено: файл не прочитан, запись стёрла бы прежние записи");
                return;
            }
            try
            {
                FileHelper.WriteAllTextAtomic(_path, JsonConvert.SerializeObject(_data, Formatting.Indented));
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "Ошибка трекинга версий");
            }
        }

        public void TrackInstall(string appId, string installedVersion, string latestVersion)
        {
            _data[appId] = new TrackedInstall
            {
                InstalledVersion = installedVersion,
                LatestVersionAtInstall = latestVersion,
                InstalledAt = DateTime.UtcNow
            };
            Save();
        }
    }

    public class TrackedInstall
    {
        public string InstalledVersion { get; set; } = "";
        public string LatestVersionAtInstall { get; set; } = "";
        public DateTime InstalledAt { get; set; }
    }
}
