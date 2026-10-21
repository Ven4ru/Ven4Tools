using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    /// <summary>
    /// «Износ батареи ноутбука»: заводская и текущая ёмкость и циклы из WMI root\wmi,
    /// запасной путь — отчёт <c>powercfg /batteryreport /xml</c>. Только чтение.
    /// Возвращает null, если батареи нет (настольный ПК) — тогда блок скрыт.
    /// </summary>
    internal static class BatteryHealthService
    {
        public static Task<BatteryHealthResult?> ReadAsync(CancellationToken ct = default) =>
            Task.Run(async () =>
            {
                if (!HasBattery()) return null;

                var wmi = BatteryHealthCalculator.Evaluate(BatteryHealthCalculator.Combine(ReadWmi()));
                if (wmi.HasData && wmi.Cycles != null) return wmi;

                // WMI не дал ёмкость или циклы — пробуем отчёт powercfg; берём его, только
                // если он дал больше достоверного, чем WMI.
                var report = await ReadPowercfgAsync(ct);
                if (report is { HasData: true } && (!wmi.HasData || (wmi.Cycles == null && report.Cycles != null)))
                    return report;
                return (BatteryHealthResult?)wmi;
            }, ct);

        private static bool HasBattery()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_Battery");
                using var batteries = searcher.Get();
                return batteries.Count > 0;
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "BatteryHealthService.HasBattery");
                return false;
            }
        }

        private static IReadOnlyList<BatteryRaw> ReadWmi()
        {
            // Классы root\wmi связаны по InstanceName: у каждой батареи своя запись.
            var designed = ReadByInstance("BatteryStaticData", "DesignedCapacity");
            var full = ReadByInstance("BatteryFullChargedCapacity", "FullChargedCapacity");
            var cycles = ReadByInstance("BatteryCycleCount", "CycleCount");
            var names = designed.Keys.Union(full.Keys).Union(cycles.Keys).ToList();
            return names.Select(n => new BatteryRaw(
                designed.TryGetValue(n, out long d) ? d : null,
                full.TryGetValue(n, out long f) ? f : null,
                cycles.TryGetValue(n, out long c) ? c : null)).ToList();
        }

        private static Dictionary<string, long> ReadByInstance(string className, string property)
        {
            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var scope = new ManagementScope(@"root\wmi");
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT InstanceName, {property} FROM {className}"));
                using var items = searcher.Get();
                foreach (ManagementObject item in items)
                {
                    using (item)
                    {
                        string name = item["InstanceName"]?.ToString() ?? "";
                        if (item[property] != null) result[name] = Convert.ToInt64(item[property]);
                    }
                }
            }
            catch (Exception ex)
            {
                // На части ноутбуков класса нет (драйвер батареи его не публикует) — это не сбой.
                AppLogger.Write($"🔋 {className} недоступен: {ex.Message}");
            }
            return result;
        }

        private static async Task<BatteryHealthResult?> ReadPowercfgAsync(CancellationToken ct)
        {
            string path = Path.Combine(Path.GetTempPath(), $"Ven4Tools-battery-{Guid.NewGuid():N}.xml");
            try
            {
                var (code, _) = await KitCommandRunner.Default.RunAsync(
                    Path.Combine(Environment.SystemDirectory, "powercfg.exe"),
                    new[] { "/batteryreport", "/xml", "/output", path },
                    TimeSpan.FromSeconds(30), ct);
                if (code != 0 || !File.Exists(path))
                {
                    AppLogger.Write($"🔋 powercfg /batteryreport не дал отчёт (код {code})");
                    return null;
                }
                var batteries = BatteryHealthCalculator.ParsePowercfgXml(await File.ReadAllTextAsync(path, ct));
                return batteries.Count == 0 ? null : BatteryHealthCalculator.Evaluate(BatteryHealthCalculator.Combine(batteries));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "BatteryHealthService.ReadPowercfg");
                return null;
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) { AppLogger.Write($"🔋 Не удалось убрать временный отчёт батареи: {ex.Message}"); }
            }
        }
    }
}
