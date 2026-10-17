using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Общая блокировка Windows Installer и окно Windows «Установить программное
    /// обеспечение для данного устройства?» во время установки.
    /// </summary>
    /// <remarks>
    /// Найдено на стенде 17.10.2026 (весь каталог на 6.3.0): пакет choco RaiDrive ставил
    /// драйвер EldoS, и Windows показала окно доверия издателю драйвера. В тихом наборе
    /// его некому было нажать. Через 10 минут клиент прервал choco, но окно, DrvInst и
    /// серверные процессы msiexec остались — блокировка Windows Installer держалась до
    /// конца набора (1 ч 43 мин), и все следующие MSI-установки упали с кодом 1618
    /// «Другая установка уже выполняется».
    /// Чистая логика (разбор командных строк, выбор процессов) — отдельными функциями,
    /// см. InstallerLockGuardTests.
    /// </remarks>
    internal static class InstallerLockGuard
    {
        /// <summary>Мьютекс, который Windows Installer держит, пока идёт установка.</summary>
        internal const string MsiMutexName = @"Global\_MSIExecute";

        /// <summary>Сколько ждать, пока Windows Installer освободится, перед очередным источником.</summary>
        internal static readonly TimeSpan MsiFreeWaitLimit = TimeSpan.FromMinutes(3);

        /// <summary>Сколько дать прерванной установке доиграть самой, прежде чем снимать её остатки.</summary>
        internal static readonly TimeSpan LeftoverGrace = TimeSpan.FromSeconds(30);

        private static readonly TimeSpan PromptPollInterval = TimeSpan.FromSeconds(5);

        /// <summary>Процесс, относящийся к установке, — то, что нужно для решений ниже.</summary>
        internal sealed record InstallerProcess(int Pid, string Name, string? CommandLine, DateTime StartedUtc);

        // ── Чистая логика ──────────────────────────────────────────────────────

        /// <summary>
        /// Командная строка окна доверия драйверу:
        /// <c>rundll32.exe C:\WINDOWS\system32\pnpui.dll,InstallSecurityPromptRunDllW ...</c>
        /// </summary>
        internal static bool IsDriverPromptCommandLine(string? commandLine) =>
            !string.IsNullOrEmpty(commandLine)
            && commandLine.Contains("pnpui.dll", StringComparison.OrdinalIgnoreCase)
            && commandLine.Contains("InstallSecurityPrompt", StringComparison.OrdinalIgnoreCase);

        /// <summary>Путь к .inf драйвера из командной строки окна доверия; null — не нашёлся.</summary>
        internal static string? ExtractInfPath(string? commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return null;
            // Аргументы rundll32 идут без кавычек, а в пути бывают пробелы: путь — от
            // ближайшего к «.inf» начала «X:\», а не от первого в строке (pnpui.dll).
            var m = Regex.Match(commandLine, @"[A-Za-z]:\\(?:(?![A-Za-z]:\\)[^""<>|*?\r\n])*?\.inf\b", RegexOptions.IgnoreCase);
            return m.Success ? m.Value : null;
        }

        /// <summary>
        /// Издатель драйвера из текста .inf: <c>Provider</c> в разделе [Version], ссылка
        /// вида <c>%Name%</c> разворачивается по разделу [Strings]. null — не указан.
        /// </summary>
        internal static string? ParseInfProvider(string? infText)
        {
            if (string.IsNullOrEmpty(infText)) return null;
            string? section = null;
            string? provider = null;
            var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in infText.Split('\n'))
            {
                string line = raw.Trim();
                int comment = line.IndexOf(';');
                if (comment >= 0) line = line[..comment].Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    section = line[1..^1].Trim();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim();
                string value = line[(eq + 1)..].Trim().Trim('"');
                if (string.Equals(section, "Version", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(key, "Provider", StringComparison.OrdinalIgnoreCase))
                    provider = value;
                else if (string.Equals(section, "Strings", StringComparison.OrdinalIgnoreCase))
                    strings[key] = value;
            }
            if (string.IsNullOrWhiteSpace(provider)) return null;
            if (provider.Length > 2 && provider.StartsWith('%') && provider.EndsWith('%'))
                return strings.TryGetValue(provider[1..^1], out var resolved) && !string.IsNullOrWhiteSpace(resolved)
                    ? resolved
                    : null;
            return provider;
        }

        /// <summary>
        /// Остатки прерванной установки: окно доверия драйверу, DrvInst и серверные
        /// процессы msiexec (<c>-Embedding</c>), запущенные не раньше <paramref name="sinceUtc"/>.
        /// Более старые — чужие, их не трогаем. Служба msiexec (<c>/V</c>) в список не
        /// попадает никогда: она общая и сама завершится без дела.
        /// </summary>
        internal static List<InstallerProcess> SelectLeftovers(IEnumerable<InstallerProcess> processes, DateTime sinceUtc)
        {
            var result = new List<InstallerProcess>();
            foreach (var p in processes)
            {
                if (p.StartedUtc < sinceUtc) continue;
                bool leftover = p.Name.ToLowerInvariant() switch
                {
                    "rundll32.exe" => IsDriverPromptCommandLine(p.CommandLine),
                    "drvinst.exe" => true,
                    "msiexec.exe" => p.CommandLine?.Contains("-Embedding", StringComparison.OrdinalIgnoreCase) == true,
                    _ => false
                };
                if (leftover) result.Add(p);
            }
            return result;
        }

        /// <summary>
        /// Неудача из-за занятого Windows Installer (1618 у MSI, «установка уже идёт»
        /// у winget) — такую программу имеет смысл повторить, когда установщик освободится.
        /// </summary>
        internal static bool IsMsiBusyFailure(string? message) =>
            !string.IsNullOrEmpty(message) && message.Contains(MsiExitCodes.Known[1618], StringComparison.Ordinal);

        // ── Система ────────────────────────────────────────────────────────────

        /// <summary>Идёт ли сейчас установка Windows Installer (любая, не только наша).</summary>
        internal static bool IsMsiBusy()
        {
            try
            {
                if (!Mutex.TryOpenExisting(MsiMutexName, out var mutex)) return false;
                mutex.Dispose();
                return true;
            }
            catch (UnauthorizedAccessException) { return true; } // мьютекс есть, но нам его не открыть
            catch { return false; }
        }

        /// <summary>
        /// Перед очередным источником: если Windows Installer занят, ждём его до
        /// <see cref="MsiFreeWaitLimit"/>, а не падаем сразу с 1618.
        /// </summary>
        /// <returns>false — установщик так и не освободился (источник всё равно пробуем).</returns>
        internal static async Task<bool> WaitForMsiFreeAsync(Action<string> log, CancellationToken token)
        {
            if (!IsMsiBusy()) return true;
            log($"⏳ Windows Installer занят другой установкой — ждём до {(int)MsiFreeWaitLimit.TotalMinutes} мин");
            var deadline = DateTime.UtcNow + MsiFreeWaitLimit;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                if (!IsMsiBusy())
                {
                    log("✅ Windows Installer освободился");
                    return true;
                }
            }
            log("⚠️ Windows Installer всё ещё занят — продолжаем, установка может не пройти");
            return false;
        }

        /// <summary>
        /// После неудачной или прерванной попытки: если Windows Installer всё ещё занят,
        /// даём ему <paramref name="grace"/> доиграть, потом завершаем остатки попытки
        /// (<see cref="SelectLeftovers"/>). Без этого одна зависшая установка роняла
        /// все следующие MSI набора с кодом 1618.
        /// </summary>
        internal static async Task ReleaseLeftoversAsync(DateTime sinceUtc, TimeSpan grace, Action<string> log)
        {
            try
            {
                var deadline = DateTime.UtcNow + grace;
                while (IsMsiBusy() && DateTime.UtcNow < deadline)
                    await Task.Delay(TimeSpan.FromSeconds(2));
                if (!IsMsiBusy()) return;

                var leftovers = SelectLeftovers(await Task.Run(Snapshot), sinceUtc);
                if (leftovers.Count == 0) return;
                foreach (var p in leftovers)
                {
                    try
                    {
                        using var proc = System.Diagnostics.Process.GetProcessById(p.Pid);
                        proc.Kill();
                    }
                    catch { }
                }
                log($"🧹 Остатки прерванной установки сняты ({string.Join(", ", leftovers.Select(p => $"{p.Name} PID {p.Pid}"))}) — Windows Installer свободен для следующих программ");
            }
            catch (Exception ex) { AppLogger.Write($"[InstallerLockGuard] Снятие остатков установки: {ex.Message}"); }
        }

        /// <summary>
        /// Следит за окном доверия драйверу, пока идёт установка. В тихом режиме окно
        /// закрывается («Не устанавливать») — ответить некому, а ждать до тайм-аута
        /// значит держать блокировку Windows Installer. В обычном — поднимается на
        /// передний план: оно часто открывается позади окна клиента.
        /// </summary>
        internal static IDisposable WatchDriverPrompts(DateTime sinceUtc, bool silent, Action<string> log)
        {
            var handled = new HashSet<int>();
            var gate = new object();
            var timer = new Timer(_ =>
            {
                if (!Monitor.TryEnter(gate)) return;
                try
                {
                    foreach (var p in Snapshot())
                    {
                        if (p.StartedUtc < sinceUtc || !p.Name.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase)
                            || !IsDriverPromptCommandLine(p.CommandLine) || !handled.Add(p.Pid))
                            continue;
                        HandleDriverPrompt(p, silent, log);
                    }
                }
                catch (Exception ex) { AppLogger.Write($"[InstallerLockGuard] Окно доверия драйверу: {ex.Message}"); }
                finally { Monitor.Exit(gate); }
            }, null, PromptPollInterval, PromptPollInterval);
            return timer;
        }

        private static void HandleDriverPrompt(InstallerProcess prompt, bool silent, Action<string> log)
        {
            string? publisher = null;
            try
            {
                string? inf = ExtractInfPath(prompt.CommandLine);
                if (inf != null && File.Exists(inf)) publisher = ParseInfProvider(File.ReadAllText(inf));
            }
            catch { }
            string who = publisher != null ? $" от «{publisher}»" : "";

            using var proc = System.Diagnostics.Process.GetProcessById(prompt.Pid);
            if (silent)
            {
                try { proc.Kill(); } catch { }
                log($"🛡️ Windows спросила, устанавливать ли драйвер{who}. В тихом режиме ответить некому — окно закрыто («Не устанавливать»), установщик продолжит без драйвера");
                return;
            }
            log($"🛡️ Windows спрашивает, устанавливать ли драйвер{who}. Ответьте в окне «Безопасность Windows» — установка ждёт ответа");
            IntPtr window = proc.MainWindowHandle;
            if (window != IntPtr.Zero)
            {
                ShowWindow(window, SwRestore);
                SetForegroundWindow(window);
            }
        }

        /// <summary>Процессы, к которым относятся решения выше: rundll32, DrvInst и msiexec.</summary>
        private static List<InstallerProcess> Snapshot()
        {
            var list = new List<InstallerProcess>();
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, Name, CommandLine, CreationDate FROM Win32_Process " +
                "WHERE Name = 'rundll32.exe' OR Name = 'DrvInst.exe' OR Name = 'msiexec.exe'");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    DateTime started = DateTime.MinValue;
                    if (mo["CreationDate"] is string created)
                    {
                        try { started = ManagementDateTimeConverter.ToDateTime(created).ToUniversalTime(); } catch { }
                    }
                    list.Add(new InstallerProcess(
                        Convert.ToInt32(mo["ProcessId"]),
                        mo["Name"] as string ?? "",
                        mo["CommandLine"] as string,
                        started));
                }
            }
            return list;
        }

        private const int SwRestore = 9;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
