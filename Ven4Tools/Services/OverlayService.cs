using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Ven4Tools.Views;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Оверлей поверх игр: FPS окна, которое сейчас на переднем плане, и короткие
    /// показания процессора и видеокарты раз в секунду. Включается кнопкой в
    /// «Диагностике» или сочетанием Ctrl+Shift+O. Сочетание регистрируется только после
    /// первого включения — у того, кто оверлеем не пользуется, клавиши не перехватываются.
    /// </summary>
    public sealed class OverlayService
    {
        public static OverlayService Instance { get; } = new();
        public const string HotkeyText = "Ctrl+Shift+O";

        private const int HotkeyId = 0x5634;
        private const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000, VK_O = 0x4F;
        private const int WM_HOTKEY = 0x0312;

        private OverlayWindow? _window;
        private DispatcherTimer? _timer;
        private HwndSource? _hotkeySink;
        private bool _ticking;

        public event Action? StateChanged;
        public bool IsVisible => _window != null;
        public bool IsExtended { get; private set; }

        // Расширенный вид: история загрузки за 60 секунд и максимумы с включения оверлея.
        private readonly Queue<double> _cpuLoad = new(), _gpuLoad = new();
        private OverlaySessionMax _session = new();

        private OverlayService() { }

        /// <summary>
        /// Кнопка и Ctrl+Shift+O — по кругу: компактный → расширенный → выключен.
        /// Из игры выходить не нужно.
        /// </summary>
        public void Toggle()
        {
            if (!IsVisible) Show();
            else if (!IsExtended) { IsExtended = true; AppLogger.Write("🎮 Оверлей: расширенный вид"); StateChanged?.Invoke(); OnTick(null, EventArgs.Empty); }
            else Hide();
        }

        public void Show()
        {
            if (_window != null) return;
            EnsureHotkey();
            // В игре с античитом оверлей не включается, пока человек сам не разрешил это
            // в «Диагностике»: окно поверх игры античиты видят.
            if (AntiCheatDetector.Active() is { } game && !ProfileService.Current.OverlayInAntiCheatGames)
            {
                AppLogger.Write($"🛡 Оверлей не включён: запущена игра с античитом {AntiCheatDetector.ShortName(game)}");
                AntiCheatGuard.Notify?.Invoke(Tr("Оверлей не включён"),
                    string.Format(Tr("Запущена игра с античитом {0}. Разрешить оверлей в таких играх можно галочкой в «Диагностике» — на свой риск."), AntiCheatDetector.ShortName(game)));
                return;
            }
            _ = HardwareSensorService.Instance.AcquireAsync("overlay");
            IsExtended = false;
            _session = new OverlaySessionMax();
            _cpuLoad.Clear(); _gpuLoad.Clear();
            bool fps = PresentMonService.Instance.Start();
            _window = new OverlayWindow();
            _window.SetLines(new[] { fps ? "FPS  …" : Tr("FPS: нужен счётчик (кнопка в «Диагностике»)") });
            _window.Show();
            _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += OnTick;
            _timer.Start();
            Application.Current.Exit -= OnAppExit;
            Application.Current.Exit += OnAppExit;
            AppLogger.Write("🎮 Оверлей включён");
            StateChanged?.Invoke();
        }

        public void Hide()
        {
            if (_timer != null) { _timer.Stop(); _timer.Tick -= OnTick; }
            _window?.Close();
            _window = null;
            IsExtended = false;
            PresentMonService.Instance.Stop();
            _ = HardwareSensorService.Instance.ReleaseAsync("overlay");
            AppLogger.Write("🎮 Оверлей выключен");
            StateChanged?.Invoke();
        }

        private async void OnTick(object? sender, EventArgs e)
        {
            if (_ticking || _window == null) return;
            _ticking = true;
            try
            {
                // PresentMon мог закрыться сам (сбой, чужой «убить процесс») — установлен,
                // значит, поднять снова, а не просить поставить его ещё раз.
                if (!PresentMonService.Instance.IsRunning && PresentMonService.FindExecutable() != null)
                    PresentMonService.Instance.Start();
                bool counter = PresentMonService.Instance.IsRunning;
                int pid = ForegroundProcessId();
                var (frames, recent) = counter
                    ? PresentMonService.Instance.StatsFor(pid, ProcessExeName(pid))
                    : (new FrameStats(null, null, null), Array.Empty<double>());

                var raw = await HardwareSensorService.Instance.ReadRawAsync();
                if (_window == null) return;
                _session.Update(raw);
                Push(_cpuLoad, HardwareSnapshotBuilder.Find(raw.Where(x => x.Device == SensorDevice.Cpu).ToList(), SensorKind.Load, "CPU Total"));
                Push(_gpuLoad, HardwareSnapshotBuilder.Find(ExtendedOverlay.MainGpu(raw), SensorKind.Load, "GPU Core"));

                if (IsExtended)
                {
                    var lines = ExtendedOverlay.Build(raw, frames, ReadRam(), _session, DateTime.Now).ToList();
                    if (!counter) lines[0] = new OverlayLine(Tr("FPS: нужен счётчик (кнопка в «Диагностике»)"), OverlayLineStyle.Header);
                    _window.SetExtended(lines, recent, _cpuLoad.ToList(), _gpuLoad.ToList());
                }
                else
                {
                    var lines = new List<string>
                    {
                        !counter ? Tr("FPS: нужен счётчик (кнопка в «Диагностике»)")
                        : frames.Fps != null ? $"FPS  {Math.Round(frames.Fps.Value):0}" : "FPS  —"
                    };
                    lines.AddRange(HardwareSnapshotBuilder.OverlayLines(raw));
                    _window.SetLines(lines);
                }
                _window.SetMascot(ProfileService.Current.OverlayShowMascot ? ExtendedOverlay.Mood(raw) : null);
                _window.KeepOnTop();
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "OverlayService.Tick");
            }
            finally { _ticking = false; }
        }

        private static void Push(Queue<double> history, float? value)
        {
            history.Enqueue(value ?? 0);
            while (history.Count > 60) history.Dequeue();
        }

        private static RamInfo? ReadRam()
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref m)) return null;
            const double gb = 1024.0 * 1024 * 1024;
            return new RamInfo((m.ullTotalPhys - m.ullAvailPhys) / gb, m.ullTotalPhys / gb);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        private void OnAppExit(object? sender, ExitEventArgs e)
        {
            PresentMonService.Instance.Stop();
            if (_hotkeySink != null) UnregisterHotKey(_hotkeySink.Handle, HotkeyId);
        }

        // Служебное окно только для сообщений: ловит сочетание клавиш, даже когда главное
        // окно свёрнуто в трей.
        private void EnsureHotkey()
        {
            if (_hotkeySink != null) return;
            _hotkeySink = new HwndSource(new HwndSourceParameters("Ven4ToolsOverlayHotkey") { ParentWindow = new IntPtr(-3) });
            _hotkeySink.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId) { Toggle(); handled = true; }
                return IntPtr.Zero;
            });
            if (!RegisterHotKey(_hotkeySink.Handle, HotkeyId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_O))
                AppLogger.Write($"🎮 Сочетание {HotkeyText} занято другой программой — оверлей включается только кнопкой");
        }

        private static string? ProcessExeName(int pid)
        {
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.ProcessName + ".exe"; }
            catch { return null; }
        }

        private static int ForegroundProcessId()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            return (int)pid;
        }

        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    }
}
