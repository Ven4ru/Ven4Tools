using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Ven4Tools.Helpers;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    internal static class PresetService
    {
        private static readonly string LocalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "presets.json");

        public static Task<List<Preset>> LoadAsync() => Task.FromResult(LoadLocal() ?? new());

        // Все изменяющие методы: LoadLocal() == null — файл не прочитан, писать нельзя
        // (иначе сохранили бы пустой список поверх непрочитанных пресетов).
        public static Task<Preset?> SaveAsync(Preset preset)
        {
            var local = LoadLocal();
            if (local == null) return Task.FromResult<Preset?>(null);
            if (preset.Id == 0)
            {
                int newId;
                do { newId = Random.Shared.Next(int.MinValue + 1, 0); }
                while (local.Exists(p => p.Id == newId));
                preset.Id = newId;
                local.Add(preset);
            }
            else
            {
                var index = local.FindIndex(p => p.Id == preset.Id);
                if (index >= 0) local[index] = preset; else local.Add(preset);
            }
            if (!SaveLocal(local)) return Task.FromResult<Preset?>(null);
            return Task.FromResult<Preset?>(preset);
        }

        public static Task<bool> DeleteAsync(Preset preset)
        {
            var local = LoadLocal();
            if (local == null) return Task.FromResult(false);
            local.RemoveAll(p => p.Id == preset.Id);
            return Task.FromResult(SaveLocal(local));
        }

        /// <summary>
        /// Атомарно заменяет весь список пресетов одной записью файла.
        /// Возвращает false, если запись на диск не удалась.
        /// </summary>
        public static Task<bool> ReplaceAllAsync(List<Preset> presets)
            => Task.FromResult(SaveLocal(presets));

        public static Task<bool> UpdateAsync(Preset preset)
        {
            var local = LoadLocal();
            if (local == null) return Task.FromResult(false);
            var index = local.FindIndex(p => p.Id == preset.Id);
            if (index < 0) return Task.FromResult(false);
            local[index] = preset;
            return Task.FromResult(SaveLocal(local));
        }

        private static List<Preset>? LoadLocal() =>
            JsonListFile.TryLoad<Preset>(LocalPath, "[PresetService]");

        private static bool SaveLocal(List<Preset> list)
        {
            try
            {
                FileHelper.WriteAllTextAtomic(LocalPath,
                    JsonConvert.SerializeObject(list, Formatting.Indented));
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[PresetService] Сохранение локальных пресетов: {ex.Message}");
                return false;
            }
        }
    }
}
