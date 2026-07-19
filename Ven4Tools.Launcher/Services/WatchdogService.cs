using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using Ven4Tools.Launcher;

namespace Ven4Tools.Launcher.Services
{
    public sealed class WatchdogService : IDisposable
    {
        private static readonly string HeartbeatPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "heartbeat.json");

        private static readonly string CrashPath = LauncherPaths.CrashReportPath;

        private const int CheckIntervalSec  = 10;
        private const int FreezeTimeoutSec  = 20;

        private readonly Process _process;
        private readonly Timer   _timer;
        private bool _disposed;

        // OnTick (таймер) и ReportKill (Process.Exited) могут сработать почти
        // одновременно на один и тот же инцидент — без флага пользователь видит
        // два окна отчёта подряд (freeze + kill) об одном и том же вылете.
        // Interlocked гарантирует, что событие уйдёт только один раз.
        private int _reported;

        // Клиент завис — лаунчер предлагает завершить
        public event Action<CrashReport>? ClientFrozen;

        // Клиент убит извне (TaskMgr, kill) без крэш-файла
        public event Action<CrashReport>? ClientKilledWithoutCrash;

        public WatchdogService(Process process)
        {
            _process = process;
            _timer   = new Timer(OnTick, null,
                TimeSpan.FromSeconds(CheckIntervalSec),
                TimeSpan.FromSeconds(CheckIntervalSec));
        }

        private void OnTick(object? _)
        {
            if (_disposed) return;
            try
            {
                // Процесс уже завершился — Exited событие разберётся
                if (_process.HasExited) return;

                var beat = ReadHeartbeat();
                if (beat == null) return;

                // heartbeat.json может остаться от прошлого запуска клиента и быть
                // устаревшим уже на момент первого тика — сверяем PID, чтобы не поднять
                // ложный "завис" по чужому (прошлому) процессу.
                if (beat.Value.pid != _process.Id) return;

                double age = (DateTime.UtcNow - beat.Value.timestamp).TotalSeconds;
                if (age < FreezeTimeoutSec) return;

                if (Interlocked.CompareExchange(ref _reported, 1, 0) != 0) return;

                // Клиент завис
                Stop();

                var report = new CrashReport
                {
                    SessionId     = ExtractSessionId(),
                    MachineName   = Environment.MachineName,
                    Version       = beat.Value.version,
                    Timestamp     = DateTime.UtcNow.ToString("O"),
                    OsVersion     = Environment.OSVersion.ToString(),
                    ExceptionType = "ProcessFrozen",
                    Message       = $"Клиент не отвечал {(int)age} сек (PID {beat.Value.pid})",
                    StackTrace    = $"Последний heartbeat: {beat.Value.timestamp:HH:mm:ss} UTC",
                    Reported      = false
                };

                ClientFrozen?.Invoke(report);
            }
            catch { }
        }

        // Вызывается из обработчика Process.Exited клиента (LaunchExistingClient),
        // если свежего crash_last.json не найдено
        public void ReportKill(int exitCode)
        {
            if (_disposed) return;
            if (Interlocked.CompareExchange(ref _reported, 1, 0) != 0) return;
            var report = new CrashReport
            {
                SessionId     = ExtractSessionId(),
                MachineName   = Environment.MachineName,
                Version       = ReadHeartbeat()?.version ?? "unknown",
                Timestamp     = DateTime.UtcNow.ToString("O"),
                OsVersion     = Environment.OSVersion.ToString(),
                ExceptionType = "ProcessKilled",
                Message       = $"Клиент завершён принудительно (код выхода: {exitCode})",
                StackTrace    = "Процесс был убит извне — стек недоступен.",
                Reported      = false
            };
            ClientKilledWithoutCrash?.Invoke(report);
        }

        private static (DateTime timestamp, int pid, string version)? ReadHeartbeat()
        {
            try
            {
                if (!File.Exists(HeartbeatPath)) return null;
                var obj = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(
                    File.ReadAllText(HeartbeatPath));
                if (obj == null) return null;

                // Newtonsoft по умолчанию парсит ISO-строки в DateTime, поэтому
                // берём значение как DateTime напрямую и приводим к UTC.
                // (Раньше каст (string)obj.Timestamp ломал round-trip формат и watchdog молча не срабатывал.)
                DateTime ts      = obj.Value<DateTime>("Timestamp").ToUniversalTime();
                int pid          = obj.Value<int>("Pid");
                string version   = obj.Value<string>("Version") ?? "unknown";
                return (ts, pid, version);
            }
            catch { return null; }
        }

        private static string ExtractSessionId()
        {
            try
            {
                if (!File.Exists(CrashPath)) return "UNKNOWN";
                var r = JsonConvert.DeserializeObject<CrashReport>(
                    File.ReadAllText(CrashPath));
                return r?.SessionId ?? "UNKNOWN";
            }
            catch { return "UNKNOWN"; }
        }

        public void Stop() =>
            _timer.Change(Timeout.Infinite, Timeout.Infinite);

        public void Dispose()
        {
            _disposed = true;
            _timer.Dispose();
        }
    }
}
