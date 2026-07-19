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

        public VersionTrackingService() => Load();

        private void Load()
        {
            try
            {
                if (File.Exists(_path))
                    _data = JsonConvert.DeserializeObject<Dictionary<string, TrackedInstall>>(
                        File.ReadAllText(_path)) ?? new();
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[VersionTrackingService] Чтение данных трекинга версий: {ex.Message}");
                _data = new();
            }
        }

        private void Save()
        {
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
