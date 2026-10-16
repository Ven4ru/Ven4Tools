using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Замер «признаков работы» установки для <see cref="InstallStallDetector"/>:
    /// байты ввода-вывода процесса и всех его потомков плюс объём файлов в папке загрузки.
    /// Любая ошибка замера даёт просто меньшее число — сторож от этого только осторожнее
    /// (неизменный показатель лишь приближает срок, но не прерывает работающее раньше срока).
    /// </summary>
    internal static class ProcessActivityProbe
    {
        public static long Sample(int rootPid, string? downloadFolder)
        {
            long total = 0;
            try
            {
                foreach (int pid in TreeOf(rootPid))
                    total += IoBytes(pid);
            }
            catch { }
            if (!string.IsNullOrEmpty(downloadFolder))
                total += FolderBytes(downloadFolder);
            return total;
        }

        /// <summary>
        /// Есть ли у процесса или его потомков видимое главное окно. В тихом режиме
        /// окно означает, что программа ждёт ответа пользователя.
        /// </summary>
        public static bool HasVisibleWindow(int rootPid)
        {
            try
            {
                foreach (int pid in TreeOf(rootPid))
                {
                    try
                    {
                        using var p = System.Diagnostics.Process.GetProcessById(pid);
                        if (p.MainWindowHandle != IntPtr.Zero) return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        /// <summary>Корень и все его потомки по снимку списка процессов.</summary>
        internal static List<int> TreeOf(int rootPid)
        {
            var parentOf = new Dictionary<int, int>();
            IntPtr snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snap == InvalidHandle) return new List<int> { rootPid };
            try
            {
                var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
                if (Process32First(snap, ref entry))
                {
                    do { parentOf[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID; }
                    while (Process32Next(snap, ref entry));
                }
            }
            finally { CloseHandle(snap); }

            var tree = new List<int> { rootPid };
            for (int i = 0; i < tree.Count; i++)
                foreach (var kv in parentOf)
                    if (kv.Value == tree[i] && kv.Key != tree[i] && !tree.Contains(kv.Key))
                        tree.Add(kv.Key);
            return tree;
        }

        private static long IoBytes(int pid)
        {
            IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (h == IntPtr.Zero) return 0;
            try
            {
                return GetProcessIoCounters(h, out var c)
                    ? (long)(c.ReadTransferCount + c.WriteTransferCount + c.OtherTransferCount)
                    : 0;
            }
            finally { CloseHandle(h); }
        }

        private static long FolderBytes(string folder)
        {
            long sum = 0;
            try
            {
                if (!Directory.Exists(folder)) return 0;
                foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    try { sum += f.Length; } catch { }
                }
            }
            catch { }
            return sum;
        }

        private const uint Th32csSnapProcess = 0x00000002;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private static readonly IntPtr InvalidHandle = new(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32FirstW")]
        private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32NextW")]
        private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
