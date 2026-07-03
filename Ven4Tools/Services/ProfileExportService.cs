using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Экспорт и импорт локальной конфигурации одним zip-файлом.
    /// Замена облачной синхронизации после её удаления: пользователь сам
    /// переносит файл на новый ПК, данные не покидают устройство.
    /// </summary>
    public static class ProfileExportService
    {
        private static readonly string BaseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools");

        // Белый список файлов конфигурации. При импорте принимаются только записи
        // архива с этими именами в корне — защита от path traversal и от подмены
        // служебных файлов (логи, device_id и т.п. не переносятся).
        private static readonly string[] AllowedFiles =
        {
            "profile.json",      // ProfileService: тема, язык, режим каталога, закреплённые
            "presets.json",      // PresetService: пользовательские пресеты
            "favorites.json",    // FavoritesService: избранное
            "settings.json",     // AppSettings: уведомления, таймауты
            "source_order.json", // SourceOrderService: порядок источников установки
        };

        // Максимальный размер одного файла при распаковке — защита от zip-бомбы
        private const long MaxEntrySize = 10 * 1024 * 1024;

        public sealed class OperationResult
        {
            public bool Success { get; init; }
            public int FilesProcessed { get; init; }
            public string Message { get; init; } = "";
        }

        /// <summary>
        /// Собирает существующие локальные файлы конфигурации в zip-архив по указанному пути.
        /// Запись атомарная: сначала во временный файл, затем перемещение.
        /// </summary>
        public static OperationResult Export(string zipPath)
        {
            string tmp = zipPath + "." + Path.GetRandomFileName() + ".tmp";
            try
            {
                var existing = new List<string>();
                foreach (var name in AllowedFiles)
                    if (File.Exists(Path.Combine(BaseDir, name)))
                        existing.Add(name);

                if (existing.Count == 0)
                    return new OperationResult
                    {
                        Success = false,
                        Message = "Нет данных для экспорта: локальные файлы настроек не найдены."
                    };

                using (var zipStream = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
                {
                    foreach (var name in existing)
                        archive.CreateEntryFromFile(Path.Combine(BaseDir, name), name,
                            CompressionLevel.Optimal);
                }

                File.Move(tmp, zipPath, overwrite: true);
                return new OperationResult
                {
                    Success = true,
                    FilesProcessed = existing.Count,
                    Message = $"Экспортировано файлов: {existing.Count} → {Path.GetFileName(zipPath)}"
                };
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[ProfileExportService] Export: {ex.Message}");
                return new OperationResult
                {
                    Success = false,
                    Message = $"Ошибка экспорта: {ex.Message}"
                };
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        /// <summary>
        /// Распаковывает архив и заменяет локальные файлы конфигурации.
        /// Каждый файл заменяется атомарно и независимо (best-effort):
        /// повреждённая или лишняя запись не прерывает импорт остальных.
        /// После замены перечитывает состояние сервисов и уведомляет подписчиков.
        /// </summary>
        public static OperationResult Import(string zipPath)
        {
            int replaced = 0, errors = 0;
            try
            {
                Directory.CreateDirectory(BaseDir);
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        // Принимаем только записи из белого списка в корне архива
                        bool allowed = false;
                        foreach (var name in AllowedFiles)
                            if (string.Equals(entry.FullName, name, StringComparison.OrdinalIgnoreCase))
                            { allowed = true; break; }
                        if (!allowed) continue;

                        var target = Path.Combine(BaseDir, entry.Name);
                        var tmp = target + "." + Path.GetRandomFileName() + ".tmp";
                        try
                        {
                            // Лимит проверяется по фактически распакованным байтам:
                            // entry.Length — лишь заявленный размер из метаданных архива,
                            // он не проверяется при декомпрессии, и zip-бомба может
                            // объявить малый размер, а распаковать гигабайты.
                            using (var src = entry.Open())
                            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                            {
                                var buffer = new byte[16 * 1024];
                                long total = 0;
                                int read;
                                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    total += read;
                                    if (total > MaxEntrySize)
                                        throw new InvalidDataException(
                                            "запись слишком велика, распаковка прервана");
                                    dst.Write(buffer, 0, read);
                                }
                            }
                            File.Move(tmp, target, overwrite: true);
                            replaced++;
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Write($"[ProfileExportService] Import {entry.FullName}: {ex.Message}");
                            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                            errors++;
                        }
                    }
                }
            }
            catch (InvalidDataException)
            {
                return new OperationResult
                {
                    Success = false,
                    Message = "Файл повреждён или не является архивом настроек Ven4Tools."
                };
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[ProfileExportService] Import: {ex.Message}");
                return new OperationResult
                {
                    Success = false,
                    Message = $"Ошибка импорта: {ex.Message}"
                };
            }

            if (replaced == 0)
                return new OperationResult
                {
                    Success = false,
                    Message = errors > 0
                        ? "Импорт не выполнен: файлы в архиве не удалось прочитать."
                        : "В архиве не найдено файлов настроек Ven4Tools."
                };

            // Перечитываем состояние сервисов, чтобы каталог пересчитал фильтры без
            // перезапуска. Избранное кэшируется вкладкой каталога при создании —
            // для него нужен перезапуск приложения.
            try
            {
                ProfileService.Reload();
                AppSettings.NotifyChanged();
                SourceOrderService.Load();
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[ProfileExportService] Import (обновление сервисов): {ex.Message}");
            }

            return new OperationResult
            {
                Success = true,
                FilesProcessed = replaced,
                Message = errors > 0
                    ? $"Импортировано файлов: {replaced}, с ошибками: {errors}"
                    : $"Импортировано файлов: {replaced}"
            };
        }
    }
}
