using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Ven4Tools.Services
{
    /// <summary>Процесс, который держит файл открытым.</summary>
    public sealed record LockingProcess(
        int ProcessId,
        string Name,
        string? ExePath,
        DateTime? StartTimeUtc,
        bool IsSystem,
        bool IsService,
        bool RunsFromTarget);

    /// <summary>Итог поиска: процессы и сколько файлов просмотрено.</summary>
    public sealed record FileLockResult(IReadOnlyList<LockingProcess> Processes, int FilesChecked, bool Truncated);

    /// <summary>
    /// «Кто держит файл»: штатный Restart Manager (rstrtmgr.dll) — тот же механизм,
    /// которым пользуются установщики Windows, чтобы показать «закройте программу X».
    /// Ничего не меняет в системе: сессия Restart Manager только читает список.
    /// Для папки перебираются файлы (с ограничением числа и отменой), плюс
    /// процессы, запущенные из этой папки.
    /// </summary>
    public static class FileLockService
    {
        /// <summary>Сколько файлов папки проверяем максимум — иначе на папке с сотнями тысяч файлов поиск идёт минутами.</summary>
        public const int MaxFolderFiles = 5000;

        private const int RegisterChunk = 200;
        private const int ErrorMoreData = 234;
        private const int CchRmSessionKey = 32;

        /// <summary>
        /// Имена процессов ядра и сеанса Windows: им кнопка «Завершить» не положена —
        /// завершение приводит к синему экрану, выходу из сеанса или потере сети/звука.
        /// </summary>
        private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "Memory Compression", "Secure System",
            "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
            "svchost", "dwm", "fontdrvhost", "LsaIso", "spoolsv", "audiodg",
            "SearchIndexer", "MsMpEng", "NisSrv", "sihost", "ctfmon", "conhost",
        };

        /// <summary>Чистая проверка «системный ли процесс» — покрыта тестами.</summary>
        public static bool IsSystemProcess(string processName, int processId, int sessionId, bool isCritical, bool isService)
        {
            if (processId is 0 or 4) return true;               // Idle и System
            if (isCritical || isService) return true;
            if (sessionId == 0) return true;                     // нулевой сеанс — службы
            string name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName[..^4] : processName;
            return CriticalNames.Contains(name);
        }

        /// <summary>
        /// Найти процессы, которые держат файл или файлы папки. Блокирующий вызов —
        /// запускать в фоне (Task.Run).
        /// </summary>
        public static FileLockResult Find(string path, CancellationToken ct)
        {
            var files = new List<string>();
            bool truncated = false;
            bool isFolder = Directory.Exists(path);
            if (isFolder)
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                foreach (string f in Directory.EnumerateFiles(path, "*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    if (files.Count >= MaxFolderFiles) { truncated = true; break; }
                    files.Add(f);
                }
            }
            else if (File.Exists(path))
            {
                files.Add(path);
            }
            else
            {
                throw new FileNotFoundException("Файл или папка не найдены.", path);
            }

            var found = new Dictionary<int, LockingProcess>();
            if (files.Count > 0)
            {
                foreach (var p in QueryRestartManager(files, ct))
                    found[p.ProcessId] = p;
            }

            // Программа, запущенная из папки, держит свой exe и библиотеки — это и есть
            // частая причина «папка не удаляется». Restart Manager её обычно тоже видит,
            // но не всегда (например, если exe из папки, а файлы — нет).
            if (isFolder)
            {
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
                foreach (Process proc in Process.GetProcesses())
                {
                    using (proc)
                    {
                        ct.ThrowIfCancellationRequested();
                        string? exe = TryGetExePath(proc.Id);
                        if (exe == null || !exe.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                        if (found.TryGetValue(proc.Id, out var existing))
                        {
                            found[proc.Id] = existing with { RunsFromTarget = true };
                            continue;
                        }
                        int session = SafeSessionId(proc);
                        found[proc.Id] = new LockingProcess(proc.Id, proc.ProcessName, exe, SafeStartTime(proc),
                            IsSystemProcess(proc.ProcessName, proc.Id, session, false, false), false, true);
                    }
                }
            }

            // Сам клиент тоже может держать файл (например, только что выбранный в диалоге) —
            // показываем его, но завершать себя не даём.
            int self = Environment.ProcessId;
            if (found.TryGetValue(self, out var me)) found[self] = me with { IsSystem = true };

            var list = found.Values.OrderBy(p => p.IsSystem).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            return new FileLockResult(list, files.Count, truncated);
        }

        private static List<LockingProcess> QueryRestartManager(List<string> files, CancellationToken ct)
        {
            var key = new StringBuilder(CchRmSessionKey + 1);
            int rc = RmStartSession(out uint session, 0, key);
            if (rc != 0) throw new Win32Exception(rc);
            try
            {
                for (int i = 0; i < files.Count; i += RegisterChunk)
                {
                    ct.ThrowIfCancellationRequested();
                    string[] chunk = files.Skip(i).Take(RegisterChunk).ToArray();
                    rc = RmRegisterResources(session, (uint)chunk.Length, chunk, 0, null, 0, null);
                    if (rc != 0) throw new Win32Exception(rc);
                }

                ct.ThrowIfCancellationRequested();
                uint needed = 0, count = 0, reasons = 0;
                RM_PROCESS_INFO[]? infos = null;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    rc = RmGetList(session, out needed, ref count, infos, ref reasons);
                    if (rc == 0) break;
                    if (rc != ErrorMoreData) throw new Win32Exception(rc);
                    // Список мог вырасти между вызовами — берём с запасом.
                    count = needed + 4;
                    infos = new RM_PROCESS_INFO[count];
                }
                if (rc != 0) throw new Win32Exception(rc);

                var result = new List<LockingProcess>();
                for (int i = 0; i < count && infos != null; i++)
                {
                    RM_PROCESS_INFO info = infos[i];
                    int pid = info.Process.dwProcessId;
                    string? exe = TryGetExePath(pid);
                    string name = !string.IsNullOrWhiteSpace(info.strAppName)
                        ? info.strAppName
                        : exe != null ? Path.GetFileNameWithoutExtension(exe) : $"PID {pid}";
                    bool isService = info.ApplicationType == RM_APP_TYPE.RmService;
                    bool isCritical = info.ApplicationType == RM_APP_TYPE.RmCritical;
                    string procName = exe != null ? Path.GetFileNameWithoutExtension(exe) : name;
                    DateTime start = DateTime.FromFileTimeUtc(
                        ((long)info.Process.ProcessStartTime.dwHighDateTime << 32) |
                        (uint)info.Process.ProcessStartTime.dwLowDateTime);
                    result.Add(new LockingProcess(pid, name, exe, start,
                        IsSystemProcess(procName, pid, (int)info.TSSessionId, isCritical, isService),
                        isService, false));
                }
                return result;
            }
            finally
            {
                RmEndSession(session);
            }
        }

        /// <summary>
        /// Тот ли это ещё процесс: за время, пока пользователь читал список, PID мог
        /// освободиться и достаться другой программе — завершать её нельзя.
        /// </summary>
        public static Process? OpenSameProcess(LockingProcess target)
        {
            try
            {
                var proc = Process.GetProcessById(target.ProcessId);
                if (target.StartTimeUtc is { } expected)
                {
                    DateTime? actual = SafeStartTime(proc);
                    if (actual is { } a && Math.Abs((a - expected).TotalSeconds) > 1)
                    {
                        proc.Dispose();
                        return null;
                    }
                }
                return proc;
            }
            catch (ArgumentException)
            {
                return null; // процесс уже завершился
            }
        }

        private static DateTime? SafeStartTime(Process p)
        {
            try { return p.StartTime.ToUniversalTime(); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return null; }
        }

        private static int SafeSessionId(Process p)
        {
            try { return p.SessionId; }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return 0; }
        }

        /// <summary>Полный путь exe без чтения модулей процесса (работает и для 32-битных, и для защищённых).</summary>
        public static string? TryGetExePath(int pid)
        {
            IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(h); }
        }

        // ── P/Invoke ─────────────────────────────────────────────────────────

        private const uint ProcessQueryLimitedInformation = 0x1000;

        private enum RM_APP_TYPE
        {
            RmUnknownApp = 0,
            RmMainWindow = 1,
            RmOtherWindow = 2,
            RmService = 3,
            RmExplorer = 4,
            RmConsole = 5,
            RmCritical = 1000,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
            public RM_APP_TYPE ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

        [DllImport("rstrtmgr.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
            uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
            [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
