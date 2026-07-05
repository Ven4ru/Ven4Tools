using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Ven4Tools.Helpers;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public static class SourceOrderService
    {
        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "source_order.json");

        public static SourceOrderSettings Current { get; private set; } = new();

        // Fires after Save() — CatalogTab subscribes to re-check availability
        public static event Action? Changed;

        static SourceOrderService() => Load();

        public static void Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var loaded = JsonConvert.DeserializeObject<SourceOrderSettings>(
                        File.ReadAllText(_path));
                    if (loaded != null)
                    {
                        // Migrate the previous untouched default. Preserve every
                        // genuinely customized order.
                        var legacyDefault = new[]
                        {
                            SourceOrderSettings.Winget,
                            SourceOrderSettings.Choco,
                            "scoop",
                            SourceOrderSettings.Direct
                        };
                        if (loaded.GlobalOrder.SequenceEqual(legacyDefault))
                            loaded.GlobalOrder = new List<string>(SourceOrderSettings.AllSources);

                        // Защитная очистка для сохранённых настроек, где ещё остался
                        // удалённый источник Scoop: убираем "scoop" из глобального порядка
                        // и все категории, где он был назначен основным (GetOrderForCategory
                        // корректно фолбэкнется на глобальный порядок).
                        loaded.GlobalOrder.RemoveAll(s => s == "scoop");
                        foreach (var key in loaded.CategoryPrimary
                                     .Where(kv => kv.Value == "scoop")
                                     .Select(kv => kv.Key)
                                     .ToList())
                            loaded.CategoryPrimary.Remove(key);

                        // Ensure all sources are present in GlobalOrder (forward compat)
                        foreach (var s in SourceOrderSettings.AllSources)
                            if (!loaded.GlobalOrder.Contains(s))
                                loaded.GlobalOrder.Add(s);
                        Current = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "Ошибка загрузки порядка источников");
            }
        }

        public static void Save()
        {
            try
            {
                FileHelper.WriteAllTextAtomic(_path, JsonConvert.SerializeObject(Current, Formatting.Indented));
                Changed?.Invoke();
            }
            catch (Exception ex) { AppLogger.Write($"[SourceOrderService] Save: {ex.Message}"); }
        }

        // Returns effective source order for a category.
        // When per_category mode: category primary goes first, rest from GlobalOrder.
        // Falls back to GlobalOrder when no override set.
        public static List<string> GetOrderForCategory(string? categoryName = null)
        {
            if (Current.Mode == "per_category"
                && !string.IsNullOrEmpty(categoryName)
                && Current.CategoryPrimary.TryGetValue(categoryName, out var primary)
                && !string.IsNullOrEmpty(primary))
            {
                var order = new List<string> { primary };
                foreach (var s in Current.GlobalOrder)
                    if (s != primary) order.Add(s);
                return order;
            }
            return new List<string>(Current.GlobalOrder);
        }

        public static void SetCategoryPrimary(string category, string sourceId)
        {
            Current.CategoryPrimary[category] = sourceId;
        }

        public static string GetCategoryPrimary(string category) =>
            Current.CategoryPrimary.TryGetValue(category, out var v) ? v : "";
    }
}
