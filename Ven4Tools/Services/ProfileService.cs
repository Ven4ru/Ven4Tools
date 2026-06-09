using System;
using System.IO;
using Newtonsoft.Json;
using Ven4Tools.Helpers;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public static class ProfileService
    {
        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "profile.json");

        public static UserProfile Current { get; private set; } = new();
        public static event Action? Changed;

        static ProfileService() => Load();

        public static void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var profile = JsonConvert.DeserializeObject<UserProfile>(File.ReadAllText(_path));
                if (profile != null) Current = profile;
            }
            catch (Exception ex) { AppLogger.Write($"[ProfileService] {ex.Message}"); }
        }

        public static void Save()
        {
            try
            {
                FileHelper.WriteAllTextAtomic(_path, JsonConvert.SerializeObject(Current, Formatting.Indented));
                Changed?.Invoke();
            }
            catch (Exception ex) { AppLogger.Write($"[ProfileService] {ex.Message}"); }
        }

        public static void Reset(bool keepCategorySelection = true)
        {
            bool had = Current.HasSelectedCategory;
            Current = new UserProfile();
            if (keepCategorySelection) Current.HasSelectedCategory = had;
            Save();
        }
    }
}
