using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Shared;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Счётчик кадров через Intel PresentMon (открытый код, MIT, подпись Intel). PresentMon
    /// читает события Windows о показе кадров (ETW) и в игры не внедряется — античиты
    /// такой способ не трогают, в отличие от перехвата отрисовки, как у RTSS.
    ///
    /// В сборку Ven4Tools он не входит: ставится по кнопке через winget из пакета
    /// <c>Intel.PresentMon.Console</c>. Перед каждым запуском проверяется подпись Intel —
    /// файл лежит в папке пользователя, а запускает его клиент с правами администратора.
    /// </summary>
    public sealed class PresentMonService
    {
        public const string WingetId = "Intel.PresentMon.Console";
        private const string SessionName = "Ven4ToolsFps";

        public static PresentMonService Instance { get; } = new();

        private readonly object _gate = new();
        private readonly Dictionary<int, Queue<(double At, double FrameMs)>> _frames = new();
        private readonly Dictionary<int, string> _names = new();
        private Process? _process;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private PresentMonService() { }

        public bool IsRunning { get { lock (_gate) return _process is { HasExited: false }; } }

        /// <summary>Копия с CDN, если winget не справился: %LOCALAPPDATA%\Ven4Tools\Tools\PresentMon.</summary>
        private static string MirrorPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "Tools", "PresentMon", MirrorDownload.PresentMon.FileName);

        /// <summary>Установленный PresentMon (winget или копия с CDN); null — не установлен.</summary>
        public static string? FindExecutable()
        {
            string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages");
            string? winget = Directory.Exists(packages)
                ? Directory.EnumerateDirectories(packages, WingetId + "_*")
                    .SelectMany(d => Directory.EnumerateFiles(d, "PresentMon*.exe", SearchOption.AllDirectories))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
            return winget ?? (File.Exists(MirrorPath) ? MirrorPath : null);
        }

        public static async Task<PawnIoDriver.Result> InstallAsync(CancellationToken ct = default)
        {
            var (code, _) = await WingetRunner.RunAsync(new[]
            {
                "install", "--id", WingetId, "--exact", "--source", "winget", "--silent",
                "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"
            }, TimeSpan.FromMinutes(5), ct);
            AppLogger.Write($"[PresentMon] winget install: код {code}");
            // winget не скачал файл с GitHub — та же версия с нашего CDN (сумма сверяется).
            if (FindExecutable() == null) await MirrorDownload.DownloadAsync(MirrorDownload.PresentMon, MirrorPath, ct);
            string? exe = FindExecutable();
            if (exe == null)
                return new PawnIoDriver.Result(false, string.Format(Tr("Счётчик FPS не установился (winget, код {0})."), code));
            if (!AuthenticodeVerifier.IsSignedByOrganization(exe, new[] { "Intel Corporation" }, out string error))
            {
                AppLogger.Write($"[PresentMon] подпись не подтверждена ({exe}): {error}");
                return new PawnIoDriver.Result(false, Tr("Подпись PresentMon не подтвердилась — счётчик FPS не будет запускаться."));
            }
            await InstallHistoryService.Instance.TrackAsync("presentmon", "Intel PresentMon — счётчик FPS", "winget", "Системные");
            return new PawnIoDriver.Result(true, Tr("Счётчик FPS (Intel PresentMon) установлен."));
        }

        /// <summary>Запустить сбор кадров; false — PresentMon не установлен или подпись не та.</summary>
        public bool Start()
        {
            lock (_gate)
            {
                if (_process is { HasExited: false }) return true;
                string? exe = FindExecutable();
                if (exe == null) return false;
                if (!AuthenticodeVerifier.IsSignedByOrganization(exe, new[] { "Intel Corporation" }, out string error))
                {
                    AppLogger.Write($"[PresentMon] не запущен: подпись не подтверждена ({error})");
                    return false;
                }

                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                foreach (string arg in new[]
                         {
                             "--output_stdout", "--no_console_stats", "--stop_existing_session",
                             "--session_name", SessionName, "--exclude", "dwm.exe",
                             "--no_track_input", "--no_track_gpu"
                         })
                    psi.ArgumentList.Add(arg);

                _frames.Clear();
                _names.Clear();
                _process = Process.Start(psi);
                if (_process == null) return false;
                _process.OutputDataReceived += (_, e) => { if (e.Data != null) OnLine(e.Data); };
                _process.ErrorDataReceived += (_, e) => { };
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                AppLogger.Write("🎮 Счётчик FPS запущен");
                return true;
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_process == null) return;
                try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
                catch (Exception ex) { AppLogger.Write(ex, "PresentMonService.Stop"); }
                _process.Dispose();
                _process = null;
                _frames.Clear();
                AppLogger.Write("🎮 Счётчик FPS остановлен");
            }
        }

        // Столбцы CSV PresentMon 2.x: Application, ProcessID, …, MsBetweenPresents (10),
        // MsBetweenDisplayChange (11). Номера берём из заголовка, а не наизусть.
        private int _pidCol = 1, _presentsCol = 10, _displayCol = 11;

        private void OnLine(string line)
        {
            if (line.StartsWith("Application,", StringComparison.Ordinal))
            {
                var head = line.Split(',');
                _pidCol = Array.IndexOf(head, "ProcessID");
                _presentsCol = Array.IndexOf(head, "MsBetweenPresents");
                _displayCol = Array.IndexOf(head, "MsBetweenDisplayChange");
                return;
            }
            if (FpsMath.TryParseFrame(line, _pidCol, _displayCol, _presentsCol, out int pid, out double frameMs))
            {
                double now = _clock.Elapsed.TotalMilliseconds;
                string app = line[..Math.Max(0, line.IndexOf(','))];
                lock (_gate)
                {
                    _names[pid] = app;
                    if (!_frames.TryGetValue(pid, out var q)) _frames[pid] = q = new Queue<(double, double)>();
                    q.Enqueue((now, frameMs));
                    while (q.Count > 0 && now - q.Peek().At > 10000) q.Dequeue();
                }
            }
        }

        /// <summary>
        /// Кадры окна на переднем плане: FPS за последнюю секунду, 1 % low и среднее время
        /// кадра за 10 секунд, времена последних кадров для графика. Браузеры и часть игр
        /// рисуют кадры отдельным процессом того же приложения (msedge.exe, chrome.exe) —
        /// тогда берётся самый частый по кадрам процесс с тем же именем файла.
        /// </summary>
        public (FrameStats Stats, double[] RecentFrameMs) StatsFor(int pid, string? exeName)
        {
            lock (_gate)
            {
                double now = _clock.Elapsed.TotalMilliseconds;
                Queue<(double At, double FrameMs)>? best = null;
                double bestFps = 0;
                void Consider(Queue<(double At, double FrameMs)> q)
                {
                    double fps = FpsMath.Fps(q.Where(f => now - f.At <= 1000).Select(f => f.FrameMs).ToList()) ?? 0;
                    if (fps > bestFps) { bestFps = fps; best = q; }
                }
                if (_frames.TryGetValue(pid, out var own)) Consider(own);
                if (best == null && !string.IsNullOrEmpty(exeName))
                    foreach (var n in _names.Where(n => n.Value.Equals(exeName, StringComparison.OrdinalIgnoreCase)))
                        if (_frames.TryGetValue(n.Key, out var q)) Consider(q);
                if (best == null) return (new FrameStats(null, null, null), Array.Empty<double>());

                var window = best.Select(f => f.FrameMs).ToList();
                return (new FrameStats(bestFps, FpsMath.OnePercentLow(window), window.Average()),
                        best.Where(f => now - f.At <= 5000).Select(f => f.FrameMs).ToArray());
            }
        }

        public double? FpsFor(int pid, string? exeName) => StatsFor(pid, exeName).Stats.Fps;
    }

    /// <summary>Разбор строки PresentMon и расчёт FPS — без процессов, для модульных проверок.</summary>
    public static class FpsMath
    {
        public static bool TryParseFrame(string line, int pidCol, int displayCol, int presentsCol, out int pid, out double frameMs)
        {
            pid = 0; frameMs = 0;
            var cells = line.Split(',');
            if (pidCol < 0 || pidCol >= cells.Length || !int.TryParse(cells[pidCol], out pid)) return false;
            // Время между сменами кадра на экране — то, что видит человек; если кадр не
            // дошёл до экрана (NA), берём время между вызовами Present.
            foreach (int col in new[] { displayCol, presentsCol })
            {
                if (col >= 0 && col < cells.Length
                    && double.TryParse(cells[col], NumberStyles.Float, CultureInfo.InvariantCulture, out frameMs)
                    && frameMs > 0)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 1 % low: FPS по среднему времени самых долгих 1 % кадров (не меньше одного кадра).
        /// Именно эти кадры глаз видит как подёргивания; среднее FPS их прячет. Меньше 20
        /// кадров — считать рано.
        /// </summary>
        public static double? OnePercentLow(IReadOnlyList<double> frameMs)
        {
            if (frameMs.Count < 20) return null;
            int worst = Math.Max(1, frameMs.Count / 100);
            double avg = frameMs.OrderByDescending(x => x).Take(worst).Average();
            return avg > 0 ? 1000.0 / avg : null;
        }

        public static double? Fps(IReadOnlyList<double> frameMs)
        {
            if (frameMs.Count < 2) return null;
            double avg = frameMs.Average();
            return avg > 0 ? 1000.0 / avg : null;
        }
    }
}
