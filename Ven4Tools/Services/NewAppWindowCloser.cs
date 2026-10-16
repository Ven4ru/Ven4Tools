using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Закрывает окна, которые только что установленная программа открыла сама
    /// (Opera, Discord, PowerToys, f.lux и др. запускаются после тихой установки).
    /// Только для наборов и тихого режима — решение владельца 16.10.2026; одиночная
    /// установка по кнопке ничего не закрывает.
    /// </summary>
    /// <remarks>
    /// Чужое окно не трогается: закрывается только процесс, запущенный после начала
    /// установки, чей исполняемый файл создан во время этой же установки (то есть
    /// программа поставлена только что), и только мягко — CloseMainWindow, без Kill.
    /// </remarks>
    internal static class NewAppWindowCloser
    {
        private static readonly TimeSpan[] Checks = { TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(30) };

        /// <summary>Запускает проверки в фоне и сразу возвращается — набор не ждёт.</summary>
        public static void ScheduleClose(DateTime installStartedUtc, string appName, Action<string>? log)
        {
            _ = Task.Run(async () =>
            {
                var elapsed = TimeSpan.Zero;
                foreach (var at in Checks)
                {
                    await Task.Delay(at - elapsed);
                    elapsed = at;
                    try { CloseNewWindows(installStartedUtc, appName, log); } catch { }
                }
            });
        }

        internal static bool IsFreshlyInstalled(DateTime processStartUtc, DateTime exeCreatedUtc, DateTime installStartedUtc)
            => processStartUtc >= installStartedUtc && exeCreatedUtc >= installStartedUtc.AddMinutes(-1);

        private static void CloseNewWindows(DateTime installStartedUtc, string appName, Action<string>? log)
        {
            // Живое дерево клиента — это идущие сейчас установщики (следующая программа
            // набора, winget, msiexec): их окна закрывать нельзя, это прервало бы установку.
            // Программа, которую запустил уже завершившийся установщик, в дерево не входит.
            var ownTree = new System.Collections.Generic.HashSet<int>(ProcessActivityProbe.TreeOf(Environment.ProcessId));
            string temp = Path.GetFullPath(Path.GetTempPath());
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (ownTree.Contains(p.Id) || p.MainWindowHandle == IntPtr.Zero) continue;
                        string? exe = p.MainModule?.FileName;
                        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) continue;
                        if (exe.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
                        // Распакованные во временную папку установщики и их помощники.
                        if (exe.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!IsFreshlyInstalled(p.StartTime.ToUniversalTime(), File.GetCreationTimeUtc(exe), installStartedUtc)) continue;
                        if (p.CloseMainWindow())
                            log?.Invoke($"🪟 Закрыто окно, открытое после установки «{appName}»: {Path.GetFileName(exe)}");
                    }
                    catch { }
                }
            }
        }
    }
}
