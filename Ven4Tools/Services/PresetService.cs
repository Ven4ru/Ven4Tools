using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    internal static class PresetService
    {
        private static readonly string LocalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "presets.json");

        public static Task<List<Preset>> LoadAsync(int? userId) => Task.FromResult(LoadLocal());

        public static Task<Preset?> SaveAsync(int? userId, Preset preset)
        {
            var local = LoadLocal();
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
            preset.IsLocal = true;
            preset.NeedsSync = false;
            preset.ShareCode = null;
            SaveLocal(local);
            return Task.FromResult<Preset?>(preset);
        }

        public static Task<bool> DeleteAsync(int? userId, Preset preset)
        {
            var local = LoadLocal();
            local.RemoveAll(p => p.Id == preset.Id);
            SaveLocal(local);
            return Task.FromResult(true);
        }

        public static Task<bool> UpdateAsync(int? userId, Preset preset)
        {
            var local = LoadLocal();
            var index = local.FindIndex(p => p.Id == preset.Id);
            if (index < 0) return Task.FromResult(false);
            preset.IsLocal = true;
            preset.NeedsSync = false;
            preset.ShareCode = null;
            local[index] = preset;
            SaveLocal(local);
            return Task.FromResult(true);
        }

        public static Task<string?> ShareAsync(int presetId) => Task.FromResult<string?>(null);
        public static Task<Preset?> GetByCodeAsync(string code) => Task.FromResult<Preset?>(null);

        private static List<Preset> LoadLocal()
        {
            try
            {
                if (!File.Exists(LocalPath)) return new();
                return JsonConvert.DeserializeObject<List<Preset>>(
                    File.ReadAllText(LocalPath, Encoding.UTF8)) ?? new();
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[PresetService] Чтение локальных пресетов: {ex.Message}");
                return new();
            }
        }

        private static void SaveLocal(List<Preset> list)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LocalPath)!);
                File.WriteAllText(LocalPath,
                    JsonConvert.SerializeObject(list, Formatting.Indented), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[PresetService] Сохранение локальных пресетов: {ex.Message}");
            }
        }
    }
}
