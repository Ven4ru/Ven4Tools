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
        // Файл есть, но не прочитан: запись пустого набора стёрла бы избранное.
        // См. SettingsFileReader.
        private bool _loadFailed;

        public FavoritesService() => Load();

        private void Load()
        {
            try
            {
                var read = SettingsFileReader.TryRead(FilePath, "[FavoritesService]", out string json);
                _loadFailed = read == SettingsReadResult.Unreadable;
                if (read != SettingsReadResult.Read) return;

                List<string>? ids;
                try { ids = JsonConvert.DeserializeObject<List<string>>(json); }
                catch (JsonException ex)
                {
                    _loadFailed = !SettingsFileReader.SetAsideCorrupt(FilePath, "[FavoritesService]", ex.Message);
                    return;
                }
                if (ids != null)
                    foreach (var id in ids)
                        _favorites.Add(id);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "Ошибка загрузки избранного");
            }
        }

        // Перед изменением: если при старте файл не прочитался, пробуем ещё раз.
        // false — файл по-прежнему недоступен, менять и писать нельзя.
        private bool EnsureLoaded()
        {
            if (!_loadFailed) return true;
            Load();
            return !_loadFailed;
        }

        public void Save()
        {
            try
            {
                if (!EnsureLoaded())
                {
                    AppLogger.Write("[FavoritesService] Сохранение пропущено: избранное не удалось прочитать");
                    return;
                }
                FileHelper.WriteAllTextAtomic(FilePath,
                    JsonConvert.SerializeObject(new List<string>(_favorites), Formatting.Indented));
            }
            catch (Exception ex) { AppLogger.Write($"[FavoritesService] Save: {ex.Message}"); }
        }

        public bool IsFavorite(string appId) => _favorites.Contains(appId);

        public void Toggle(string appId)
        {
            // Сначала дочитываем файл: иначе изменение легло бы на пустой набор.
            if (!EnsureLoaded()) return;
            if (!_favorites.Remove(appId))
                _favorites.Add(appId);
            Save();
        }

        public IReadOnlyCollection<string> All => _favorites;
    }
}
