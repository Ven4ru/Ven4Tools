using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;
using Ven4Tools.Shared;

namespace Ven4Tools.Launcher
{
    public partial class MainWindow
    {
        // Запущен ли клиент Ven4Tools из текущей папки установки.
        private bool IsClientRunning()
        {
            var proc = FindRunningClientProcess();
            proc?.Dispose();
            return proc != null;
        }

        // Находит процесс запущенного клиента из текущей папки установки и возвращает
        // его НЕ освобождённым — вызывающий (TryCloseRunningClientAsync) сам вызывает
        // Dispose(). Остальные (непарные) найденные процессы освобождаются здесь же.
        // Если MainModule недоступен — считаем процесс совпадением: безопаснее показать
        // предупреждение лишний раз, чем оставить папку клиента в битом состоянии.
        private Process? FindRunningClientProcess()
        {
            string clientExe = Path.Combine(_clientPath, LauncherPaths.ClientExeName);
            Process[] processes;
            try { processes = Process.GetProcessesByName("Ven4Tools"); }
            catch { return null; }

            foreach (var proc in processes)
            {
                bool isMatch;
                try
                {
                    string? exePath = proc.MainModule?.FileName;
                    isMatch = string.IsNullOrEmpty(exePath) ||
                              string.Equals(exePath, clientExe, StringComparison.OrdinalIgnoreCase);
                }
                catch { isMatch = true; }

                if (isMatch)
                {
                    foreach (var other in processes) if (other != proc) other.Dispose();
                    return proc;
                }
                proc.Dispose();
            }
            return null;
        }

        // Просит запущенный elevated-клиент закрыться через именованный pipe и ждёт
        // до timeoutMs, пока процесс завершится. WM_CLOSE здесь неприменим: launcher
        // работает asInvoker, и Windows UIPI блокирует его сообщения elevated-окну.
        // Клиент сам решает, закрываться ли (см. Window_Closing_Extended в
        // Ven4Tools/MainWindow.xaml.cs — предупреждение при активной установке,
        // либо сворачивание в трей вместо закрытия при включённой у клиента
        // соответствующей настройке — тогда процесс не завершится, и этот метод
        // вернёт false по таймауту; форсированный Process.Kill() не используется).
        private async Task<bool> TryCloseRunningClientAsync(int timeoutMs = 10000)
        {
            var proc = FindRunningClientProcess();
            if (proc == null) return true;

            proc.Dispose();

            if (!await ClientControlChannel.RequestShutdownAsync())
            {
                AddLog("⚠️ Клиент не принял запрос на штатное закрытие");
                return false;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!IsClientRunning()) return true;
                await Task.Delay(500);
            }
            return false;
        }
    }
}
