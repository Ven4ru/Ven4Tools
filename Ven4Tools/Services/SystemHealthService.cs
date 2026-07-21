using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Ven4Tools.Services
{
    internal enum RebootCategory
    {
        Bsod,
        FastStartupFailure,
        PossiblePowerLoss
    }

    internal sealed class RebootEvent
    {
        public DateTime TimeCreated { get; init; }
        public int BugcheckCode { get; init; }
        public bool HasFastStartupFailureNearby { get; init; }
        public bool HasCleanShutdownNearby { get; init; }
    }

    /// <summary>
    /// Классифицирует событие Kernel-Power ID 41 («нештатное завершение
    /// работы»). Порядок проверок важен: настоящий BSOD (BugcheckCode ≠ 0)
    /// всегда побеждает сбой резюме Fast Startup, даже если оба признака
    /// присутствуют одновременно.
    /// </summary>
    internal static class RebootClassifier
    {
        public static RebootCategory? Classify(RebootEvent evt)
        {
            if (evt.HasCleanShutdownNearby) return null;
            if (evt.BugcheckCode != 0) return RebootCategory.Bsod;
            if (evt.HasFastStartupFailureNearby) return RebootCategory.FastStartupFailure;
            return RebootCategory.PossiblePowerLoss;
        }
    }

    internal sealed class RebootDiagnosis
    {
        public required DateTime TimeCreated { get; init; }
        public required RebootCategory Category { get; init; }
        public required string Summary { get; init; }
        public required string RawDetails { get; init; }
    }

    internal enum DiskHealth { Healthy, Warning, Unhealthy, Unknown }

    internal sealed class DiskHealthInfo
    {
        public required string Name { get; init; }
        public required DiskHealth Health { get; init; }
    }

    internal sealed class WindowsUpdateFailure
    {
        public required DateTime TimeCreated { get; init; }
        public required string Message { get; init; }
    }

    internal sealed class HardwareEventsSummary
    {
        public required int WheaCount { get; init; }
        public required int DisplayDriverCrashCount { get; init; }
        public required List<string> RawEntries { get; init; }
    }

    /// <summary>Снимок нужных полей EventRecord, снятый до Dispose — сам EventRecord
    /// за пределы метода чтения не выносится (IDisposable, повторное чтение после
    /// Dispose кидает ObjectDisposedException).</summary>
    internal sealed class EventRecordSnapshot
    {
        public required DateTime TimeCreated { get; init; }
        public required IReadOnlyDictionary<string, string> Data { get; init; }
        public required string Message { get; init; }
    }

    internal static class SystemHealthService
    {
        private static readonly TimeSpan LookbackWindow = TimeSpan.FromDays(7);
        private const int BootCorrelationWindowSeconds = 120;

        public static Task<List<RebootDiagnosis>> GetRebootHistoryAsync() =>
            Task.Run(() =>
            {
                var cutoff = DateTime.Now - LookbackWindow;
                var power41  = QueryEvents("System", "Microsoft-Windows-Kernel-Power", 41, cutoff);
                var boot29   = QueryEvents("System", "Microsoft-Windows-Kernel-Boot", 29, cutoff);
                var clean6006 = QueryEvents("System", "EventLog", 6006, cutoff);

                var results = new List<RebootDiagnosis>();
                foreach (var p41 in power41)
                {
                    int bugcheckCode = 0;
                    if (p41.Data.TryGetValue("BugcheckCode", out var codeStr))
                        int.TryParse(codeStr, out bugcheckCode);

                    var evt = new RebootEvent
                    {
                        TimeCreated = p41.TimeCreated,
                        BugcheckCode = bugcheckCode,
                        HasFastStartupFailureNearby = boot29.Any(b =>
                            Math.Abs((b.TimeCreated - p41.TimeCreated).TotalSeconds) <= BootCorrelationWindowSeconds),
                        HasCleanShutdownNearby = clean6006.Any(c =>
                            Math.Abs((c.TimeCreated - p41.TimeCreated).TotalSeconds) <= BootCorrelationWindowSeconds)
                    };

                    var category = RebootClassifier.Classify(evt);
                    if (category == null) continue;

                    results.Add(new RebootDiagnosis
                    {
                        TimeCreated = evt.TimeCreated,
                        Category = category.Value,
                        Summary = BuildSummary(category.Value),
                        RawDetails = $"BugcheckCode={bugcheckCode}; " +
                                     $"СбойБыстрогоЗапускаРядом={evt.HasFastStartupFailureNearby}; " +
                                     p41.Message
                    });
                }
                return results.OrderByDescending(r => r.TimeCreated).ToList();
            });

        private static string BuildSummary(RebootCategory category) => category switch
        {
            RebootCategory.Bsod =>
                "Настоящий сбой системы (BSOD) — Windows зафиксировала критическую ошибку ядра.",
            RebootCategory.FastStartupFailure =>
                "Не удалось восстановиться из «Быстрого запуска» — Windows перешла на полную холодную загрузку. Само завершение работы прошло штатно.",
            RebootCategory.PossiblePowerLoss =>
                "Возможна потеря питания или аппаратное зависание — точная причина по журналу событий не определяется.",
            _ => "Неизвестная категория"
        };

        public static Task<List<DiskHealthInfo>> GetDiskHealthAsync() =>
            Task.Run(() =>
            {
                var list = new List<DiskHealthInfo>();
                try
                {
                    var scope = new ManagementScope(@"root\Microsoft\Windows\Storage");
                    using var searcher = new ManagementObjectSearcher(scope,
                        new ObjectQuery("SELECT FriendlyName, HealthStatus FROM MSFT_PhysicalDisk"));
                    foreach (ManagementObject disk in searcher.Get())
                    {
                        using (disk)
                        {
                            ushort status = disk["HealthStatus"] != null
                                ? Convert.ToUInt16(disk["HealthStatus"]) : (ushort)5;
                            list.Add(new DiskHealthInfo
                            {
                                Name = disk["FriendlyName"]?.ToString() ?? "Диск",
                                Health = status switch
                                {
                                    0 => DiskHealth.Healthy,
                                    1 => DiskHealth.Warning,
                                    2 => DiskHealth.Unhealthy,
                                    _ => DiskHealth.Unknown
                                }
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Write(ex, "SystemHealthService.GetDiskHealthAsync");
                }
                return list;
            });

        public static Task<List<WindowsUpdateFailure>> GetWindowsUpdateFailuresAsync() =>
            Task.Run(() =>
            {
                var cutoff = DateTime.Now - LookbackWindow;
                return QueryEvents("System", "Microsoft-Windows-WindowsUpdateClient", 20, cutoff)
                    .Select(e => new WindowsUpdateFailure { TimeCreated = e.TimeCreated, Message = e.Message })
                    .OrderByDescending(f => f.TimeCreated)
                    .ToList();
            });

        public static Task<HardwareEventsSummary> GetHardwareEventsAsync() =>
            Task.Run(() =>
            {
                var cutoff = DateTime.Now - LookbackWindow;
                var whea = QueryEvents("System", "Microsoft-Windows-WHEA-Logger", null, cutoff);
                var tdr  = QueryEvents("System", null, 4101, cutoff);

                var raw = new List<string>();
                raw.AddRange(whea.Select(e => $"{e.TimeCreated:g} — аппаратная ошибка (WHEA): {e.Message}"));
                raw.AddRange(tdr.Select(e => $"{e.TimeCreated:g} — сбой видеодрайвера (TDR): {e.Message}"));

                return new HardwareEventsSummary
                {
                    WheaCount = whea.Count,
                    DisplayDriverCrashCount = tdr.Count,
                    RawEntries = raw.OrderByDescending(r => r).ToList()
                };
            });

        public static bool? IsFastStartupEnabled()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
                var value = key?.GetValue("HiberbootEnabled");
                return value != null ? Convert.ToInt32(value) != 0 : null;
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "SystemHealthService.IsFastStartupEnabled");
                return null;
            }
        }

        public static async Task DisableFastStartupAsync()
        {
            var psi = new ProcessStartInfo
            {
                FileName = TrustedExecutablePaths.PowerCfgExe,
                Arguments = "/h off",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi) ?? throw new Exception("Не удалось запустить powercfg");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await stdoutTask;
            string err = await stderrTask;
            if (process.ExitCode != 0)
                throw new Exception($"powercfg завершился с ошибкой {process.ExitCode}: {err}");
        }

        public static async Task ClearWindowsUpdateCacheAsync()
        {
            await RunNetAsync("stop", "wuauserv");
            await RunNetAsync("stop", "bits");
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download");
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.GetFiles(dir))
                    {
                        try { File.Delete(file); } catch { /* файл занят службой — пропускаем */ }
                    }
                }
            }
            finally
            {
                await RunNetAsync("start", "bits");
                await RunNetAsync("start", "wuauserv");
            }
        }

        private static async Task RunNetAsync(string action, string service)
        {
            var psi = new ProcessStartInfo
            {
                FileName = TrustedExecutablePaths.NetExe,
                Arguments = $"{action} {service}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi) ?? throw new Exception("Не удалось запустить net.exe");
            await process.WaitForExitAsync();
        }

        private static List<EventRecordSnapshot> QueryEvents(
            string logName, string? providerName, int? eventId, DateTime cutoff)
        {
            var conditions = new List<string>();
            if (providerName != null) conditions.Add($"Provider[@Name='{providerName}']");
            if (eventId != null) conditions.Add($"EventID={eventId}");
            string filter = conditions.Count > 0
                ? $"*[System[{string.Join(" and ", conditions)}]]"
                : "*";

            var results = new List<EventRecordSnapshot>();
            try
            {
                var query = new EventLogQuery(logName, PathType.LogName, filter) { ReverseDirection = true };
                using var reader = new EventLogReader(query);
                while (reader.ReadEvent() is EventRecord record)
                {
                    using (record)
                    {
                        if (record.TimeCreated == null || record.TimeCreated.Value < cutoff) break;

                        string message;
                        try { message = record.FormatDescription() ?? ""; } catch { message = ""; }

                        results.Add(new EventRecordSnapshot
                        {
                            TimeCreated = record.TimeCreated.Value,
                            Data = ExtractEventData(record),
                            Message = message
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, $"SystemHealthService.QueryEvents({logName}, {providerName}, {eventId})");
            }
            return results;
        }

        private static Dictionary<string, string> ExtractEventData(EventRecord record)
        {
            var dict = new Dictionary<string, string>();
            try
            {
                var xml = System.Xml.Linq.XElement.Parse(record.ToXml());
                System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
                foreach (var data in xml.Descendants(ns + "Data"))
                {
                    var name = data.Attribute("Name")?.Value;
                    if (name != null) dict[name] = data.Value;
                }
            }
            catch { /* не удалось разобрать XML события — возвращаем то, что успели собрать */ }
            return dict;
        }
    }
}
