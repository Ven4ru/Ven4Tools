using System;
using System.IO;
using Newtonsoft.Json;

namespace Ven4Tools.Services
{
    public static class AppSettings
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "settings.json");

        public static int CatalogTimeout { get; private set; } = 10;
        public static int CheckTimeout { get; private set; } = 15;
        public static bool Notifications { get; private set; } = true;
        public static bool UpdateNotifications { get; private set; } = true;

        public static event Action? Changed;

        static AppSettings() => Reload();

        public static void Reload()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                var data = JsonConvert.DeserializeObject<SettingsData>(File.ReadAllText(SettingsPath));
                if (data == null) return;
                CatalogTimeout = data.CatalogTimeout;
                CheckTimeout = data.CheckTimeout;
                Notifications = data.Notifications;
                UpdateNotifications = data.UpdateNotifications;
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "Ошибка загрузки настроек");
            }
        }

        private sealed class SettingsData
        {
            public int CatalogTimeout { get; set; } = 10;
            public int CheckTimeout { get; set; } = 15;
            public bool Notifications { get; set; } = true;
            public bool UpdateNotifications { get; set; } = true;
        }

        public static void NotifyChanged()
        {
            Reload();
            Changed?.Invoke();
        }
    }
}
