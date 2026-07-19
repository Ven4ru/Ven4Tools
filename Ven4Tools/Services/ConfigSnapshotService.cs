using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Ven4Tools.Helpers;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Ручные локальные снапшоты конфигурации: отмеченные твики Debloater + локальные пресеты.
    /// Работает чисто на уровне данных приложения, независимо от Windows VSS
    /// (SystemRestoreService). Файлы: %LocalAppData%\Ven4Tools\snapshots\{имя}_{timestamp}.json.
    /// </summary>
    internal static class ConfigSnapshotService
    {
        /// <summary>Максимум хранимых снапшотов — старые удаляются автоматически.</summary>
        public const int MaxSnapshots = 10;

        private static string SnapshotsDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "snapshots");

        // ── Сохранение ────────────────────────────────────────────────────────────

        /// <summary>
        /// Сохраняет снапшот текущего состояния: переданные твики Debloater
        /// плюс копию локальных пресетов. Возвращает путь к файлу или null при ошибке.
        /// </summary>
        public static async Task<string?> SaveAsync(string name, IReadOnlyCollection<string> debloatTweakIds)
        {
            try
            {
                var presets = await PresetService.LoadAsync();

                var snapshot = new ConfigSnapshot
                {
                    Name            = name.Trim(),
                    CreatedAt       = DateTime.Now,
                    DebloatTweakIds = debloatTweakIds.ToList(),
                    Presets         = presets.Select(p => new ConfigSnapshotPreset
                    {
                        Id          = p.Id,
                        Name        = p.Name,
                        Description = p.Description,
                        Apps        = new List<string>(p.Apps)
                    }).ToList()
                };

                string fileName = $"{SanitizeFileName(snapshot.Name)}_{snapshot.CreatedAt:yyyyMMdd_HHmmss}.json";
                string filePath = Path.Combine(SnapshotsDir, fileName);

                await FileHelper.WriteAllTextAtomicAsync(filePath,
                    JsonConvert.SerializeObject(snapshot, Formatting.Indented));

                TrimOldSnapshots();
                AppLogger.Write($"📸 Снапшот «{snapshot.Name}» сохранён: {fileName}");
                return filePath;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Снапшоты] Ошибка сохранения: {ex.Message}");
                return null;
            }
        }

        // ── Список / чтение / удаление ────────────────────────────────────────────

        /// <summary>Список снапшотов, новые сверху. Повреждённые файлы пропускаются.</summary>
        public static List<ConfigSnapshotInfo> GetSnapshots()
        {
            var result = new List<ConfigSnapshotInfo>();
            try
            {
                if (!Directory.Exists(SnapshotsDir)) return result;

                foreach (var file in Directory.GetFiles(SnapshotsDir, "*.json"))
                {
                    var snapshot = Load(file);
                    if (snapshot == null) continue;

                    result.Add(new ConfigSnapshotInfo
                    {
                        FilePath    = file,
                        Name        = snapshot.Name,
                        CreatedAt   = snapshot.CreatedAt,
                        TweakCount  = snapshot.DebloatTweakIds.Count,
                        PresetCount = snapshot.Presets.Count
                    });
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Снапшоты] Ошибка чтения списка: {ex.Message}");
            }
            return result.OrderByDescending(s => s.CreatedAt).ToList();
        }

        /// <summary>Читает снапшот из файла. Null — если файл повреждён или несовместим.</summary>
        public static ConfigSnapshot? Load(string filePath)
        {
            try
            {
                var snapshot = JsonConvert.DeserializeObject<ConfigSnapshot>(
                    File.ReadAllText(filePath, Encoding.UTF8));
                if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Name)) return null;
                if (snapshot.FormatVersion > 1)
                {
                    AppLogger.Write($"[Снапшоты] Неподдерживаемая версия формата ({snapshot.FormatVersion}): {Path.GetFileName(filePath)}");
                    return null;
                }
                return snapshot;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Снапшоты] Повреждённый файл {Path.GetFileName(filePath)}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Удаляет файл снапшота.</summary>
        public static bool Delete(string filePath)
        {
            try
            {
                // Защита от выхода за пределы папки снапшотов: сравнение строго
                // по границе каталога, чтобы сосед вида "snapshots_evil" не прошёл проверку
                string fullPath = Path.GetFullPath(filePath);
                string baseDir  = Path.GetFullPath(SnapshotsDir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Write($"[Снапшоты] Отказ удаления вне папки снапшотов: {filePath}");
                    return false;
                }
                if (File.Exists(fullPath)) File.Delete(fullPath);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Снапшоты] Ошибка удаления: {ex.Message}");
                return false;
            }
        }

        // ── Восстановление пресетов ───────────────────────────────────────────────

        /// <summary>
        /// Восстанавливает локальные пресеты из снапшота: текущие пресеты заменяются
        /// содержимым снимка. Финальный список собирается в памяти и пишется на диск
        /// одной операцией через PresetService — без промежуточного состояния,
        /// когда старые пресеты уже стёрты, а новые ещё не добавлены.
        /// </summary>
        public static async Task<bool> RestorePresetsAsync(ConfigSnapshot snapshot)
        {
            try
            {
                var restored = snapshot.Presets.Select(sp => new Preset
                {
                    Id          = sp.Id,
                    Name        = sp.Name,
                    Description = sp.Description,
                    Apps        = new List<string>(sp.Apps)
                }).ToList();

                if (!await PresetService.ReplaceAllAsync(restored))
                {
                    AppLogger.Write($"[Снапшоты] Не удалось записать пресеты из снапшота «{snapshot.Name}» — текущие пресеты не изменены");
                    return false;
                }

                AppLogger.Write($"📸 Пресеты восстановлены из снапшота «{snapshot.Name}»: {snapshot.Presets.Count} шт.");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Снапшоты] Ошибка восстановления пресетов: {ex.Message}");
                return false;
            }
        }

        // ── Вспомогательное ───────────────────────────────────────────────────────

        /// <summary>Оставляет не более MaxSnapshots файлов — самые старые удаляются.</summary>
        private static void TrimOldSnapshots()
        {
            try
            {
                var files = Directory.GetFiles(SnapshotsDir, "*.json")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .ToList();

                foreach (var extra in files.Skip(MaxSnapshots))
                {
                    extra.Delete();
                    AppLogger.Write($"📸 Старый снапшот удалён (лимит {MaxSnapshots}): {extra.Name}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Снапшоты] Ошибка очистки старых снапшотов: {ex.Message}");
            }
        }

        /// <summary>Приводит имя снапшота к безопасному имени файла.</summary>
        private static string SanitizeFileName(string name)
        {
            string safe = PathHelper.SanitizeFileNameComponent(name).Trim().TrimEnd('.');
            if (safe.Length == 0)  safe = "снапшот";
            if (safe.Length > 40)  safe = safe.Substring(0, 40).Trim();
            return safe;
        }
    }
}
