using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace Ven4Tools.Ears
{
    // VenchikEars.exe [--inner #RRGGBB] [--log <файл>]
    // Одна копия на сеанс: новая просит прежнюю закрыться (событие Local\Ven4ToolsEarsQuit) и занимает место.
    internal static class Program
    {
        public const string QuitEvent = @"Local\Ven4ToolsEarsQuit";
        internal static string? LogPath;

        [STAThread]
        private static void Main(string[] args)
        {
            Color? inner = null;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--inner") try { inner = ColorTranslator.FromHtml(args[i + 1]); } catch { }
                if (args[i] == "--log") LogPath = args[i + 1];
            }
            // Прежняя копия — закрыть (так же клиент меняет цвет: запускает новую).
            if (EventWaitHandle.TryOpenExisting(QuitEvent, out var old)) { using (old) old.Set(); Thread.Sleep(400); }
            using var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEvent);
            Application.EnableVisualStyles();
            var form = new EarsForm(inner);
            new Thread(() => { quit.WaitOne(); try { form.BeginInvoke(new Action(form.Close)); } catch { } }) { IsBackground = true }.Start();
            Application.Run(form);
        }

        internal static void Log(string s)
        {
            if (LogPath == null) return;
            try { System.IO.File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {s}\r\n"); } catch { }
        }
    }

    /// <summary>
    /// Окно ушек: многослойное с попиксельной прозрачностью, клики проходят насквозь, фокус не берёт,
    /// в Alt+Tab и на панели его нет. Стоит на верхнем крае панели над «Пуском».
    ///
    /// Где «Пуск»: UI Automation (только чтение) — окно панели по точке на её прямоугольнике
    /// (ABM_GETTASKBARPOS), в нём кнопка AutomationId=StartButton (Windows 11) или первая кнопка (Windows 10).
    /// Когда «Пуск» сдвигается (значки по центру): события UI Automation о сдвиге Windows 11 не присылает,
    /// поэтому — SetWinEventHook на появление/скрытие окон верхнего уровня и уничтожение уже виденных, после
    /// события «Пуск» перечитывается через 0,25/0,8/1,6 с (значки едут с анимацией). Таймеров в простое нет.
    /// UI Automation читается в фоновом потоке: из потока окна вызов зависал (проверено 04.11.2026).
    /// </summary>
    internal sealed class EarsForm : Form
    {
        private readonly Color? _innerArg;
        private AutomationElement? _start;
        private System.Windows.Rect _last = System.Windows.Rect.Empty;
        private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 250 };
        private readonly int[] _delays = { 250, 550, 800 };
        private int _step, _busy;
        private readonly HashSet<IntPtr> _seen = new();
        private Native.WinEventProc? _proc;
        private IntPtr _hookWin, _hookFg, _hookMove;
        private bool _hiddenForFullscreen;
        private readonly uint _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

        public EarsForm(Color? inner)
        {
            _innerArg = inner;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000); Size = new Size(1, 1);
            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop(); Recheck(false);
                if (++_step < _delays.Length) { _debounce.Interval = _delays[_step]; _debounce.Start(); }
            };
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80000 /*LAYERED*/ | 0x20 /*TRANSPARENT*/ | 0x80 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/ | 0x8 /*TOPMOST*/;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _proc = OnWinEvent;
            _hookWin = Native.SetWinEventHook(0x8001 /*DESTROY*/, 0x8003 /*HIDE*/, IntPtr.Zero, _proc, 0, 0, 0);
            _hookFg = Native.SetWinEventHook(0x0003 /*SYSTEM_FOREGROUND*/, 0x0003, IntPtr.Zero, _proc, 0, 0, 0);
            _hookMove = Native.SetWinEventHook(0x800B /*LOCATIONCHANGE*/, 0x800B, IntPtr.Zero, _proc, 0, 0, 0);
            FindStart();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            foreach (var h in new[] { _hookWin, _hookFg, _hookMove }) if (h != IntPtr.Zero) Native.UnhookWinEvent(h);
            base.OnFormClosed(e);
        }

        protected override void WndProc(ref Message m)
        {
            // Проводник перезапустился — панель и «Пуск» новые.
            if (_taskbarCreated != 0 && m.Msg == (int)_taskbarCreated) { _start = null; HideEars(); DelayedFind(2000); }
            else if (m.Msg is 0x001A /*SETTINGCHANGE*/ or 0x007E /*DISPLAYCHANGE*/ or 0x02E0 /*DPICHANGED*/)
            { _last = System.Windows.Rect.Empty; Kick(); }
            base.WndProc(ref m);
        }

        private void DelayedFind(int ms)
        {
            var t = new System.Windows.Forms.Timer { Interval = ms };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); FindStart(); };
            t.Start();
        }

        private void FindStart()
        {
            Task.Run(() =>
            {
                try
                {
                    var tr = Native.TaskbarRect(out _);
                    if (tr.Height <= 0) { BeginInvoke(new Action(() => DelayedFind(3000))); return; }
                    var hit = AutomationElement.FromPoint(new System.Windows.Point(tr.Left + 4, tr.Top + tr.Height / 2.0));
                    var root = hit;
                    for (var up = TreeWalker.ControlViewWalker.GetParent(root); up != null && up != AutomationElement.RootElement; up = TreeWalker.ControlViewWalker.GetParent(root)) root = up;
                    var start = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"))
                             ?? root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                    Program.Log($"«Пуск»: {(start == null ? "не найден" : start.Current.BoundingRectangle.ToString())}");
                    BeginInvoke(new Action(() =>
                    {
                        _start = start;
                        if (start == null) DelayedFind(10000); else Recheck(true);
                    }));
                }
                catch (Exception ex) { Program.Log("поиск «Пуска»: " + ex.Message); BeginInvoke(new Action(() => DelayedFind(5000))); }
            });
        }

        private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != 0 || idChild != 0 || hwnd == Handle) return;
            if (ev == 0x0003 || ev == 0x800B)
            {
                // Полный экран (игра, видео, презентация): проверяем только для активного окна.
                if (ev == 0x800B && hwnd != Native.GetForegroundWindow()) return;
                UpdateFullscreen();
                return;
            }
            if (ev == 0x8001) { if (!_seen.Remove(hwnd)) return; }
            else
            {
                if (Native.GetAncestor(hwnd, 2 /*GA_ROOT*/) != hwnd) return;
                if (ev == 0x8002) _seen.Add(hwnd); else _seen.Remove(hwnd);
            }
            Kick();
        }

        private void Kick() { _step = 0; _debounce.Interval = _delays[0]; _debounce.Stop(); _debounce.Start(); }

        private void UpdateFullscreen()
        {
            bool full = Native.IsFullscreenBusy();
            if (full == _hiddenForFullscreen) return;
            _hiddenForFullscreen = full;
            if (full) HideEars(); else { _last = System.Windows.Rect.Empty; Recheck(false); }
        }

        private void Recheck(bool force)
        {
            if (_start == null || _hiddenForFullscreen) return;
            if (Interlocked.Exchange(ref _busy, 1) == 1) { _debounce.Start(); return; }
            var start = _start;
            Task.Run(() =>
            {
                try
                {
                    var r = start.Current.BoundingRectangle;
                    BeginInvoke(new Action(() =>
                    {
                        if (!force && r == _last) return;
                        _last = r;
                        Place(r);
                    }));
                }
                catch (ElementNotAvailableException) { BeginInvoke(new Action(() => { _start = null; HideEars(); DelayedFind(1500); })); }
                catch (Exception ex) { Program.Log("чтение «Пуска»: " + ex.Message); }
                finally { Interlocked.Exchange(ref _busy, 0); }
            });
        }

        private void HideEars() => Native.ShowWindow(Handle, 0);

        private void Place(System.Windows.Rect start)
        {
            var tb = Native.TaskbarRect(out var edge);
            // Первая версия: панель внизу, без автоскрытия, «Пуск» видим.
            if (edge != 3 || Native.TaskbarAutoHide() || start.IsEmpty || start.Width <= 0) { HideEars(); return; }
            // Ушки 62×15 условных единиц и ещё 3 единицы «корня», уходящего в панель: так нет шва по её верхнему краю.
            int w = (int)Math.Round(start.Width * 1.40);
            double unit = w / 62.0 * (13.0 / 46) * 62 / 15;   // высота одной условной единицы, px
            int h = (int)Math.Round(18 * unit);
            int x = (int)Math.Round(start.Left + start.Width / 2 - w / 2.0);
            int y = tb.Top - (int)Math.Round(15.5 * unit);
            var screen = Screen.FromRectangle(tb).Bounds;
            x = Math.Max(screen.Left, Math.Min(x, screen.Right - w));   // не за край экрана (Win10: «Пуск» в углу)
            // Наружная часть — цвет самой панели над серединой «Пуска» (при прозрачности он смешан с обоями).
            var outer = Native.Pixel((int)(start.Left + start.Width / 2), tb.Top + 3);
            var inner = _innerArg ?? Native.AccentColor();
            Draw(w, h, x, y, outer, inner);
            Native.ShowWindow(Handle, 4 /*SW_SHOWNOACTIVATE*/);
            Native.SetWindowPos(Handle, new IntPtr(-1) /*TOPMOST*/, 0, 0, 0, 0, 0x0013 /*NOSIZE|NOMOVE|NOACTIVATE*/);
        }

        private void Draw(int w, int h, int x, int y, Color outer, Color inner)
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
                g.ScaleTransform(w / 62f, h / 18f);
                using var o = new SolidBrush(Color.FromArgb(255, outer));
                using var i = new SolidBrush(Color.FromArgb(230, inner));
                g.FillPath(o, Ear(1, 15.5f, 12.5f, 22.5f)); g.FillPath(o, Ear(39.5f, 15.5f, 49.5f, 61));
                g.FillRectangle(o, 1.6f, 15.2f, 20.3f, 2.8f); g.FillRectangle(o, 40.1f, 15.2f, 20.3f, 2.8f);   // корень в панели
                g.FillPath(i, Ear(6.2f, 15.5f, 13.6f, 20.2f)); g.FillPath(i, Ear(41.8f, 15.5f, 48.4f, 55.8f));
            }
            Native.UpdateLayered(Handle, bmp, x, y);
        }

        // скруглённый треугольник: основание [x0..x1] на высоте y, вершина над tipX
        private static GraphicsPath Ear(float x0, float y, float tipX, float x1)
        {
            var p = new GraphicsPath();
            var top = Math.Max(y - (x1 - x0) * .66f, .8f);
            p.AddBezier(x0, y, x0 + (tipX - x0) * .45f, y - (y - top) * .55f, tipX - 1.6f, top, tipX, top);
            p.AddBezier(tipX, top, tipX + 1.6f, top, x1 - (x1 - tipX) * .35f, top + (y - top) * .25f, x1, y);
            p.CloseFigure();
            return p;
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] private struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint msg; public uint edge; public RECT rc; public IntPtr lParam; }
        [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
        [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
        [DllImport("dwmapi.dll")] private static extern int DwmGetColorizationColor(out uint c, out bool opaque);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string s);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int W, H; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLEND { public byte Op, Flags, Alpha, Format; }
        [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pt, ref SIZE sz, IntPtr src, ref POINT sp, int key, ref BLEND b, int flags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);

        public static Rectangle TaskbarRect(out uint edge)
        {
            var d = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)) };
            SHAppBarMessage(5 /*ABM_GETTASKBARPOS*/, ref d);
            edge = d.edge;
            return Rectangle.FromLTRB(d.rc.L, d.rc.T, d.rc.R, d.rc.B);
        }

        public static bool TaskbarAutoHide()
        {
            var d = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)) };
            return ((int)SHAppBarMessage(4 /*ABM_GETSTATE*/, ref d) & 1) != 0;
        }

        // QUNS_BUSY (полноэкранное приложение), RUNNING_D3D_FULL_SCREEN, PRESENTATION_MODE
        public static bool IsFullscreenBusy() => SHQueryUserNotificationState(out var s) == 0 && s is 2 or 3 or 4;

        public static Color AccentColor() =>
            DwmGetColorizationColor(out var c, out _) == 0 ? Color.FromArgb(255, (int)(c >> 16 & 0xFF), (int)(c >> 8 & 0xFF), (int)(c & 0xFF)) : Color.FromArgb(0x52, 0xD1, 0x8F);

        /// <summary>Пиксель экрана без многослойных окон (сами ушки в него не попадают).</summary>
        public static Color Pixel(int x, int y)
        {
            var dc = GetDC(IntPtr.Zero);
            try { var c = GetPixel(dc, x, y); return c == 0xFFFFFFFF ? Color.FromArgb(32, 32, 32) : Color.FromArgb((int)(c & 0xFF), (int)(c >> 8 & 0xFF), (int)(c >> 16 & 0xFF)); }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }

        public static void UpdateLayered(IntPtr hwnd, Bitmap bmp, int x, int y)
        {
            var screen = GetDC(IntPtr.Zero); var mem = CreateCompatibleDC(screen);
            var hb = bmp.GetHbitmap(Color.FromArgb(0)); var old = SelectObject(mem, hb);
            var pt = new POINT { X = x, Y = y }; var sz = new SIZE { W = bmp.Width, H = bmp.Height }; var sp = new POINT();
            var bl = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 /*AC_SRC_ALPHA*/ };
            UpdateLayeredWindow(hwnd, screen, ref pt, ref sz, mem, ref sp, 0, ref bl, 2 /*ULW_ALPHA*/);
            SelectObject(mem, old); DeleteObject(hb); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
