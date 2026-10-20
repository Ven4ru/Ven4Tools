using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Ven4Tools.Services.Diagnostics
{
    /// <summary>Результат проверки готовности к Windows 11.</summary>
    internal sealed class Win11ReadinessReport
    {
        public required IReadOnlyList<Win11CheckResult> Checks { get; init; }
        public required Win11Verdict Verdict { get; init; }
        public required WindowsVersionInfo Windows { get; init; }
    }

    /// <summary>
    /// Сбор сведений для «Готов ли ПК к Windows 11». Только чтение: WMI, реестр, функции
    /// Windows и отчёт dxdiag во временный файл. Каждый источник опрашивается отдельно —
    /// сбой одного даёт «нет данных» в своём пункте, а не обрывает всю проверку.
    /// </summary>
    internal static class Win11ReadinessService
    {
        private static readonly TimeSpan DxDiagTimeout = TimeSpan.FromSeconds(90);

        public static async Task<Win11ReadinessReport> CheckAsync(CancellationToken ct = default)
        {
            // dxdiag — самый долгий шаг (до десятков секунд), идёт параллельно с остальным.
            var adaptersTask = RunDxDiagAsync(ct);
            var factsTask = Task.Run(CollectFacts, ct);
            var windowsTask = Task.Run(ReadWindowsInfo, ct);

            var partial = await factsTask.ConfigureAwait(false);
            var adapters = await adaptersTask.ConfigureAwait(false);
            var windows = await windowsTask.ConfigureAwait(false);

            var facts = new Win11RawFacts
            {
                TpmPresent = partial.TpmPresent, TpmEnabled = partial.TpmEnabled, TpmActivated = partial.TpmActivated,
                TpmSpecVersion = partial.TpmSpecVersion, Firmware = partial.Firmware, SecureBootEnabled = partial.SecureBootEnabled,
                PartitionStyle = partial.PartitionStyle, SystemDiskBytes = partial.SystemDiskBytes, CpuName = partial.CpuName,
                Popcnt = partial.Popcnt, Sse42 = partial.Sse42, InstalledRamKb = partial.InstalledRamKb,
                Adapters = adapters
            };
            var checks = Win11Requirements.Evaluate(facts, Win11CpuMatcher.Embedded.Match(facts.CpuName));
            return new Win11ReadinessReport { Checks = checks, Verdict = Win11Requirements.ComputeVerdict(checks), Windows = windows };
        }

        private static Win11RawFacts CollectFacts()
        {
            var (tpmPresent, tpmEnabled, tpmActivated, tpmSpec) = ReadTpm();
            var (style, diskBytes) = ReadSystemDisk();
            bool? popcnt = null, sse42 = null;
            // Инструкции x86: проверка имеет смысл только в x64/x86-процессе. Клиент собран под
            // win-x64; на ARM-ПК он работает в эмуляции, и ответ относился бы к эмулятору.
            if (RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.X86)
            {
                popcnt = System.Runtime.Intrinsics.X86.Popcnt.IsSupported;
                sse42 = System.Runtime.Intrinsics.X86.Sse42.IsSupported;
            }

            return new Win11RawFacts
            {
                TpmPresent = tpmPresent, TpmEnabled = tpmEnabled, TpmActivated = tpmActivated, TpmSpecVersion = tpmSpec,
                Firmware = ReadFirmwareKind(),
                SecureBootEnabled = ReadSecureBoot(),
                PartitionStyle = style, SystemDiskBytes = diskBytes,
                CpuName = ReadCpuName(),
                Popcnt = popcnt, Sse42 = sse42,
                InstalledRamKb = ReadInstalledRamKb()
            };
        }

        private static (bool? Present, bool? Enabled, bool? Activated, string? Spec) ReadTpm()
        {
            try
            {
                var scope = new ManagementScope(@"root\CIMV2\Security\MicrosoftTpm");
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion FROM Win32_Tpm"));
                foreach (ManagementObject tpm in searcher.Get())
                {
                    using (tpm)
                    {
                        return (true,
                            tpm["IsEnabled_InitialValue"] as bool?,
                            tpm["IsActivated_InitialValue"] as bool?,
                            tpm["SpecVersion"]?.ToString());
                    }
                }
                // Пространство имён есть, а экземпляра нет — TPM выключен в BIOS или отсутствует.
                return (false, null, null, null);
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidNamespace)
            {
                return (false, null, null, null);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "Win11ReadinessService.ReadTpm");
                return (null, null, null, null);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFirmwareType(out uint firmwareType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

        private static FirmwareKind ReadFirmwareKind()
        {
            try
            {
                if (!GetFirmwareType(out uint type)) return FirmwareKind.Unknown;
                return type switch { 1 => FirmwareKind.Bios, 2 => FirmwareKind.Uefi, _ => FirmwareKind.Unknown };
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "Win11ReadinessService.ReadFirmwareKind");
                return FirmwareKind.Unknown;
            }
        }

        private static int? ReadSecureBoot()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
                return key?.GetValue("UEFISecureBootEnabled") is int value ? value : null;
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "Win11ReadinessService.ReadSecureBoot");
                return null;
            }
        }

        /// <summary>Диск, с которого загружена Windows (MSFT_Disk.IsBoot): стиль разметки и размер.</summary>
        private static (int? Style, ulong? Bytes) ReadSystemDisk()
        {
            try
            {
                var scope = new ManagementScope(@"root\Microsoft\Windows\Storage");
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT PartitionStyle, Size FROM MSFT_Disk WHERE IsBoot = TRUE"));
                foreach (ManagementObject disk in searcher.Get())
                {
                    using (disk)
                    {
                        int? style = disk["PartitionStyle"] != null ? Convert.ToInt32(disk["PartitionStyle"]) : null;
                        ulong? size = disk["Size"] != null ? Convert.ToUInt64(disk["Size"]) : null;
                        return (style, size);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "Win11ReadinessService.ReadSystemDisk");
            }
            return (null, null);
        }

        private static string? ReadCpuName()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
                foreach (ManagementObject cpu in searcher.Get())
                    using (cpu) return cpu["Name"]?.ToString()?.Trim();
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "Win11ReadinessService.ReadCpuName");
            }
            return null;
        }

        private static ulong? ReadInstalledRamKb()
        {
            try
            {
                if (GetPhysicallyInstalledSystemMemory(out ulong kb) && kb > 0) return kb;
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "Win11ReadinessService.ReadInstalledRamKb");
            }
            return null;
        }

        /// <summary>
        /// dxdiag /x пишет XML-отчёт о DirectX во временный файл. Отдельный процесс, а не
        /// загрузка d3d12.dll в клиент: так в наш процесс не попадает драйвер видеокарты.
        /// Файл свой и после чтения удаляется.
        /// </summary>
        private static async Task<IReadOnlyList<DisplayAdapterInfo>?> RunDxDiagAsync(CancellationToken ct)
        {
            string dxdiag = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dxdiag.exe");
            if (!File.Exists(dxdiag)) return null;
            string output = Path.Combine(Path.GetTempPath(), $"ven4tools-dxdiag-{Guid.NewGuid():N}.xml");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = dxdiag,
                    ArgumentList = { "/whql:off", "/x", output },
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                if (process == null) return null;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(DxDiagTimeout);
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(); } catch (Exception) { /* уже завершился */ }
                    AppLogger.Write("[Windows 11] dxdiag не ответил вовремя — пункт DirectX 12 без данных");
                    return null;
                }
                if (!File.Exists(output)) return null;
                // Отчёт бывает в UTF-16 — кодировку определяет сам XML-парсер по BOM и заголовку.
                var doc = System.Xml.Linq.XDocument.Load(output);
                return Win11Requirements.ParseDxDiag(doc);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "Win11ReadinessService.RunDxDiagAsync");
                return null;
            }
            finally
            {
                try { if (File.Exists(output)) File.Delete(output); }
                catch (Exception ex) { AppLogger.Write(ex, "Win11ReadinessService.RunDxDiagAsync: временный файл"); }
            }
        }

        private static WindowsVersionInfo ReadWindowsInfo()
        {
            string productName = "", displayVersion = "";
            int build = 0, ubr = 0;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key != null)
                {
                    productName = key.GetValue("ProductName")?.ToString() ?? "";
                    displayVersion = key.GetValue("DisplayVersion")?.ToString() ?? key.GetValue("ReleaseId")?.ToString() ?? "";
                    int.TryParse(key.GetValue("CurrentBuildNumber")?.ToString() ?? key.GetValue("CurrentBuild")?.ToString(), out build);
                    if (key.GetValue("UBR") is int u) ubr = u;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "Win11ReadinessService.ReadWindowsInfo");
            }
            if (build == 0) build = Environment.OSVersion.Version.Build;

            var (lcuDate, lcuTitle) = ReadLastCumulativeUpdate();
            return new WindowsVersionInfo
            {
                ProductName = WindowsVersionInfo.FixProductName(productName, build),
                DisplayVersion = displayVersion,
                Build = build,
                Ubr = ubr,
                LastCumulativeUpdate = lcuDate,
                LastCumulativeUpdateTitle = lcuTitle
            };
        }

        /// <summary>
        /// Последнее успешно установленное накопительное обновление — из журнала Windows Update
        /// (COM-объект Microsoft.Update.Session, только чтение истории).
        /// </summary>
        private static (DateTime? Date, string? Title) ReadLastCumulativeUpdate()
        {
            object? session = null;
            object? searcher = null;
            try
            {
                var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
                if (type == null) return (null, null);
                session = Activator.CreateInstance(type);
                if (session == null) return (null, null);
                dynamic s = session;
                searcher = s.CreateUpdateSearcher();
                dynamic us = searcher!;
                int total = us.GetTotalHistoryCount();
                if (total <= 0) return (null, null);
                dynamic history = us.QueryHistory(0, Math.Min(total, 500));
                DateTime? best = null;
                string? bestTitle = null;
                try
                {
                    int count = history.Count;
                    for (int i = 0; i < count; i++)
                    {
                        dynamic entry = history.Item(i);
                        try
                        {
                            // Operation 1 — установка; ResultCode 2/3 — успешно (с ошибками или без).
                            int operation = entry.Operation;
                            int result = entry.ResultCode;
                            string title = entry.Title ?? "";
                            if (operation != 1 || (result != 2 && result != 3)) continue;
                            if (!WindowsVersionInfo.IsWindowsCumulativeUpdateTitle(title)) continue;
                            DateTime date = ((DateTime)entry.Date).ToLocalTime();
                            if (best == null || date > best)
                            {
                                best = date;
                                bestTitle = title;
                            }
                        }
                        finally
                        {
                            Marshal.FinalReleaseComObject(entry);
                        }
                    }
                }
                finally
                {
                    Marshal.FinalReleaseComObject(history);
                }
                return (best, bestTitle);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "Win11ReadinessService.ReadLastCumulativeUpdate");
                return (null, null);
            }
            finally
            {
                if (searcher != null && Marshal.IsComObject(searcher)) Marshal.FinalReleaseComObject(searcher);
                if (session != null && Marshal.IsComObject(session)) Marshal.FinalReleaseComObject(session);
            }
        }
    }
}
