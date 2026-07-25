using System;
using System.Collections.Generic;
using System.Management;
using System.Threading.Tasks;
using Ven4Tools.Models;

namespace Ven4Tools.Services.DiskBenchmark
{
    /// <summary>
    /// Собирает предупреждения об условиях, искажающих результат замера.
    ///
    /// Предупреждения не запрещают запуск — пользователь вправе измерить свой накопитель
    /// как есть. Жёсткая блокировка ровно одна: нехватка свободного места, потому что при
    /// ней тест физически не выполнится.
    /// </summary>
    public static class BenchmarkWarningService
    {
        /// <summary>Порог заполненности тома, после которого результат заметно падает.</summary>
        private const double CrowdedVolumePercent = 90;

        /// <summary>Чистая функция: по фактам о томе строит список предупреждений.</summary>
        public static List<string> Build(bool isSystemVolume, double usedPercent, bool bitLocker, bool removable)
        {
            var warnings = new List<string>();

            if (usedPercent > CrowdedVolumePercent)
            {
                warnings.Add(
                    $"Том заполнен на {usedPercent:F0}% — накопители в таком состоянии работают " +
                    "медленнее обычного, результат будет занижен.");
            }

            if (isSystemVolume)
            {
                warnings.Add(
                    "Выбран системный том — фоновая работа Windows идёт параллельно с замером " +
                    "и снижает результат. Для точного измерения лучше выбрать другой том.");
            }

            if (bitLocker)
            {
                warnings.Add(
                    "Том зашифрован BitLocker — шифрование забирает часть производительности, " +
                    "результат будет ниже возможностей накопителя.");
            }

            if (removable)
            {
                warnings.Add(
                    "Накопитель съёмный — скорость может ограничивать порт подключения, " +
                    "а не сам накопитель.");
            }

            return warnings;
        }

        /// <summary>Собирает факты о томе и строит предупреждения.</summary>
        public static Task<List<string>> CollectAsync(BenchmarkVolumeInfo volume) => Task.Run(() =>
            Build(volume.IsSystem, volume.UsedPercent, IsBitLockerProtected(volume.Letter), volume.IsRemovable));

        /// <summary>
        /// Единственная жёсткая проверка: тестовому файлу нужно место плюс обязательный запас,
        /// иначе том будет забит под завязку и замер потеряет смысл.
        /// </summary>
        public static bool TryValidateFreeSpace(BenchmarkVolumeInfo volume, long fileSizeBytes, out string error)
        {
            if (!volume.IsReady)
            {
                error = $"Том {volume.Letter} недоступен.";
                return false;
            }

            long required = fileSizeBytes + DiskBenchmarkEngine.FreeSpaceReserveBytes;
            if (volume.FreeBytes < required)
            {
                error =
                    $"На томе {volume.Letter} недостаточно свободного места: нужно " +
                    $"{required / 1024.0 / 1024 / 1024:F1} ГиБ, доступно " +
                    $"{volume.FreeBytes / 1024.0 / 1024 / 1024:F1} ГиБ. " +
                    "Освободите место или выберите тестовый файл меньшего размера.";
                return false;
            }

            error = "";
            return true;
        }

        /// <summary>
        /// Определяет, включено ли шифрование тома. При недоступности сведений считаем,
        /// что шифрования нет: выдумывать предупреждение на пустом месте не следует.
        /// </summary>
        private static bool IsBitLockerProtected(string letter)
        {
            try
            {
                var scope = new ManagementScope(@"root\CIMV2\Security\MicrosoftVolumeEncryption");
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
                    $"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = '{letter}'"));

                foreach (ManagementObject volume in searcher.Get())
                {
                    using (volume)
                    {
                        if (volume["ProtectionStatus"] == null) continue;
                        // 0 — выключено, 1 — включено, 2 — состояние неизвестно.
                        return Convert.ToUInt32(volume["ProtectionStatus"]) == 1;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "BenchmarkWarningService.IsBitLockerProtected");
            }

            return false;
        }
    }
}
