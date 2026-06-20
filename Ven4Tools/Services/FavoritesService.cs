using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Ven4Tools.Helpers;

namespace Ven4Tools.Services
{
    public class FavoritesService
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "favorites.json");

        private readonly HashSet<string> _favorites = new();

        public FavoritesService() => Load();

        private void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var ids = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(FilePath));
                if (ids != null)
                    foreach (var id in ids)
                        _favorites.Add(id);
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                FileHelper.WriteAllTextAtomic(FilePath, JsonConvert.SerializeObject(new List<string>(_favorites)));
            }
            catch (Exception ex) { AppLogger.Write($"[FavoritesService] Save: {ex.Message}"); }
        }

        public bool IsFavorite(string appId) => _favorites.Contains(appId);

        public void Toggle(string appId)
        {
            if (!_favorites.Remove(appId))
                _favorites.Add(appId);
            Save();
        }

        public IReadOnlyCollection<string> All => _favorites;
    }
}
