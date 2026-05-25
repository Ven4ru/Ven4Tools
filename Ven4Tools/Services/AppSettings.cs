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
                dynamic? s = JsonConvert.DeserializeObject(File.ReadAllText(SettingsPath));
                if (s == null) return;
                CatalogTimeout = (int?)s.CatalogTimeout ?? 10;
                CheckTimeout = (int?)s.CheckTimeout ?? 15;
                Notifications = (bool?)s.Notifications ?? true;
                UpdateNotifications = (bool?)s.UpdateNotifications ?? true;
            }
            catch { }
        }

        public static void NotifyChanged()
        {
            Reload();
            Changed?.Invoke();
        }
    }
}
