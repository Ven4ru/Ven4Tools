using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    /// <summary>
    /// «Здоровье дисков»: список физических дисков от Windows (MSFT_PhysicalDisk и
    /// счётчики надёжности) плюс прямые данные SMART от библиотеки датчиков. Только
    /// чтение, опрос по кнопке и при открытии раздела — не постоянно.
    /// </summary>
    internal static class DiskHealthService
    {
        public static async Task<IReadOnlyList<DiskHealthReading>> ReadAsync(CancellationToken ct = default)
        {
            var windowsTask = Task.Run(ReadWindowsDisks, ct);
            IReadOnlyList<DiskSmartData> smart;
            try
            {
                smart = await HardwareSensorService.Instance.ReadStorageSmartAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Без библиотеки остаются данные Windows — показываем их, а не пустоту.
                AppLogger.Write(ex, "DiskHealthService.ReadAsync: SMART");
                smart = Array.Empty<DiskSmartData>();
            }
            var windows = await windowsTask;
            var readings = DiskHealthMerger.MergeAll(windows, smart);
            AppLogger.Write($"🩺 Здоровье дисков: дисков {readings.Count}, со SMART {smart.Count}");
            return readings;
        }

        private static IReadOnlyList<WindowsDiskInfo> ReadWindowsDisks()
        {
            var list = new List<WindowsDiskInfo>();
            try
            {
                var scope = new ManagementScope(@"root\Microsoft\Windows\Storage");
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"));
                foreach (ManagementObject disk in searcher.Get())
                {
                    using (disk)
                    {
                        if (!int.TryParse(disk["DeviceId"]?.ToString(), out int number)) continue;
                        ushort health = disk["HealthStatus"] != null ? Convert.ToUInt16(disk["HealthStatus"]) : (ushort)5;
                        var info = new WindowsDiskInfo
                        {
                            Number = number,
                            Model = disk["FriendlyName"]?.ToString() ?? "",
                            BusType = disk["BusType"] != null ? Convert.ToUInt16(disk["BusType"]) : (ushort)0,
                            MediaType = disk["MediaType"] != null ? Convert.ToUInt16(disk["MediaType"]) : (ushort)0,
                            SizeBytes = disk["Size"] != null ? Convert.ToUInt64(disk["Size"]) : 0,
                            Status = health switch
                            {
                                0 => WindowsDiskStatus.Healthy,
                                1 => WindowsDiskStatus.Warning,
                                2 => WindowsDiskStatus.Unhealthy,
                                _ => WindowsDiskStatus.Unknown
                            }
                        };
                        list.Add(WithReliabilityCounters(disk, info));
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiskHealthService.ReadWindowsDisks");
            }
            return list;
        }

        /// <summary>Счётчики надёжности диска — то же, что Get-StorageReliabilityCounter.</summary>
        private static WindowsDiskInfo WithReliabilityCounters(ManagementObject disk, WindowsDiskInfo info)
        {
            try
            {
                using var related = disk.GetRelated("MSFT_StorageReliabilityCounter");
                foreach (ManagementObject counter in related)
                {
                    using (counter)
                    {
                        return info with
                        {
                            CounterTemperature = ToInt(counter["Temperature"]),
                            CounterPowerOnHours = ToLong(counter["PowerOnHours"]),
                            CounterWear = ToInt(counter["Wear"]),
                            CounterReadErrorsUncorrected = ToLong(counter["ReadErrorsUncorrected"]),
                            CounterWriteErrorsUncorrected = ToLong(counter["WriteErrorsUncorrected"])
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                // У USB-дисков и части контроллеров счётчиков нет — это не ошибка.
                AppLogger.Write($"🩺 Счётчики надёжности диска {info.Number} недоступны: {ex.Message}");
            }
            return info;
        }

        private static int? ToInt(object? v)
        {
            if (v == null) return null;
            try { return Convert.ToInt32(v); } catch { return null; }
        }

        private static long? ToLong(object? v)
        {
            if (v == null) return null;
            try { return Convert.ToInt64(v); } catch { return null; }
        }
    }
}
