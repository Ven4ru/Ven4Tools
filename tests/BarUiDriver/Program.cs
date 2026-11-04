using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace BarUiDriver;

// BarUiDriver.exe <папка отчёта> <папка с Ven4Bar.exe и Ven4Start.exe>
// Служебные режимы: --dummy <N> (окно со своей кнопкой), --full (полноэкранное окно)
internal static class Program
{
    static string Out = "";
    static readonly StringBuilder LogText = new();
    static int Pass, Fail, Shots;
    static readonly string ThemeFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "taskbar_theme.json");

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--dummy") { Application.Run(new Form { Text = "Окно " + args[1], Width = 420, Height = 220, StartPosition = FormStartPosition.Manual, Left = 120 + int.Parse(args[1]) * 40, Top = 90 + int.Parse(args[1]) * 40 }); return 0; }
        if (args.Length >= 1 && args[0] == "--dragsrc")
        {
            var f = new Form { Text = "Источник", Width = 260, Height = 150, StartPosition = FormStartPosition.Manual, Left = 60, Top = 60, TopMost = true };
            var l = new Label { Text = "файл.txt", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 16), BackColor = Color.FromArgb(230, 240, 255) };
            l.MouseDown += (_, _) => l.DoDragDrop(new DataObject(DataFormats.Text, "файл.txt"), DragDropEffects.Copy | DragDropEffects.Move);
            f.Controls.Add(l);
            Application.Run(f); return 0;
        }
        if (args.Length >= 1 && args[0] == "--full")
        {
            var f = new Form { Text = "Полный экран", FormBorderStyle = FormBorderStyle.None, WindowState = FormWindowState.Maximized, BackColor = Color.FromArgb(20, 24, 30), TopMost = true };
            f.Controls.Add(new Label { Text = "Полноэкранное окно (как игра или видео)", ForeColor = Color.White, Font = new Font("Segoe UI", 28), AutoSize = true, Left = 80, Top = 80 });
            Application.Run(f); return 0;
        }
        Out = args[0]; var dir = args[1];
        Directory.CreateDirectory(Out);
        string? keep = File.Exists(ThemeFile) ? File.ReadAllText(ThemeFile) : null;
        string? keepBar = File.Exists(BarFile) ? File.ReadAllText(BarFile) : null;
        int stateBefore = AutoHideState();
        try { Run(dir, stateBefore); }
        catch (Exception ex) { Check(false, "исключение " + ex); Shot("ошибка"); }
        finally
        {
            foreach (var n in new[] { "Ven4Bar", "Ven4Start" }) foreach (var p in Process.GetProcessesByName(n)) { try { p.Kill(); } catch { } }
            foreach (var p in Process.GetProcessesByName("BarUiDriver").Where(p => p.Id != Environment.ProcessId)) { try { p.Kill(); } catch { } }
            if (keep != null) File.WriteAllText(ThemeFile, keep); else File.Delete(ThemeFile);
            if (keepBar != null) File.WriteAllText(BarFile, keepBar); else File.Delete(BarFile);
        }
        Log($"ИТОГ: прошло {Pass}, не прошло {Fail}");
        Log("done");
        return Fail == 0 ? 0 : 1;
    }

    static readonly string BarFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Bar", "settings.json");
    static void BarSet(string json) { Directory.CreateDirectory(Path.GetDirectoryName(BarFile)!); File.WriteAllText(BarFile, json); }
    static void Theme(string id) { Directory.CreateDirectory(Path.GetDirectoryName(ThemeFile)!); File.WriteAllText(ThemeFile, "{\"ThemeId\":\"" + id + "\"}"); }

    static AutomationElement? Bar() => AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, "Ven4Bar"));

    static void Run(string dir, int stateBefore)
    {
        var exe = Path.Combine(dir, "Ven4Bar.exe");
        Log($"Windows {Environment.OSVersion.Version}, автоскрытие родной панели до: {stateBefore & 1}");
        var self = Environment.ProcessPath!;

        // ── 1. Снизу, тема «Венчик», родная панель — автоскрытие ──
        BarSet("{}");   // настройки по умолчанию
        Theme("venchik");
        Process.Start(new ProcessStartInfo(exe, "--hide-native") { UseShellExecute = false });
        var bar = Wait(Bar, 30);
        Check(bar != null, "Ven4Bar запустилась");
        if (bar == null) return;
        Thread.Sleep(4000);
        Check((AutoHideState() & 1) == 1, "родной панели включено автоскрытие (документированным ABM_SETSTATE)");
        var wa = Screen.PrimaryScreen!.WorkingArea; var sb = Screen.PrimaryScreen.Bounds;
        Log($"рабочая область: {wa}, экран {sb}");
        Check(wa.Bottom < sb.Bottom - 30, "рабочая область уменьшилась на высоту панели (appbar)");
        Desktop();
        Shot("01_снизу_Венчик");
        Strip("02_снизу_крупно", false);
        EarsCheck(bar);

        var dummies = Enumerable.Range(1, 4).Select(i => { var p = Process.Start(self, $"--dummy {i}")!; Thread.Sleep(600); return p; }).ToList();
        Thread.Sleep(2500);
        bar = Bar()!;
        var btn = Wait(() => bar.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Окно 2")), 10);
        Check(btn != null, "у каждого окна своя кнопка на панели");
        Click(btn); Thread.Sleep(1200);
        Shot("03_окна_активно_Окно2");
        Strip("04_окна_крупно", false);
        Click(btn); Thread.Sleep(1200);   // повторный щелчок — свернуть
        Check(IsIconic(FindByTitle("Окно 2")), "повторный щелчок сворачивает окно");

        // «Пуск» — голова Венчика открывает «Свой Пуск»
        var start = bar.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Пуск"));
        Click(start);
        var menu = Wait(() => AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, "Свой Пуск")), 25);
        Thread.Sleep(3000);
        Check(menu != null, "«Пуск» открывает «Свой Пуск» (Ven4Start)");
        if (menu != null && start != null)
        {
            var mr = menu.Current.BoundingRectangle; var sr = start.Current.BoundingRectangle;
            Check(Math.Abs((mr.Left + mr.Width / 2) - (sr.Left + sr.Width / 2)) < 30 || mr.Left <= sr.Left, "меню открылось над «Пуском» панели");
        }
        Shot("05_пуск_открыт");
        Desktop(); Thread.Sleep(800);

        foreach (var p in dummies) { try { p.Kill(); } catch { } }
        Thread.Sleep(1500);

        // полный экран — панель уходит под окно
        var full = Process.Start(self, "--full")!;
        Thread.Sleep(3500);
        Shot("06_полный_экран");
        var under = WindowFromPoint(new POINT { X = sb.Width / 2, Y = sb.Height - 10 });
        GetWindowThreadProcessId(under, out uint pidUnder);
        Check(pidUnder == full.Id, "в полноэкранном окне панель не перекрывает его");
        full.Kill(); Thread.Sleep(2000);

        // мягкое закрытие (WM_CLOSE) — программа выходит и возвращает родную панель
        SoftClose();
        bool exited = Wait(() => Process.GetProcessesByName("Ven4Bar").Length == 0 ? "" : null, 10) != null;
        Check(exited, "мягкое закрытие (WM_CLOSE) завершает Ven4Bar");
        Thread.Sleep(1500);
        Check(AutoHideState() == stateBefore, "родная панель вернулась как была");
        foreach (var p in Process.GetProcessesByName("Ven4Start")) { try { p.Kill(); } catch { } }

        // ── 2. Слева, тема «Рыжий кот» ──
        Theme("ginger");
        Process.Start(new ProcessStartInfo(exe, "--hide-native --left") { UseShellExecute = false });
        bar = Wait(Bar, 30);
        Check(bar != null, "Ven4Bar слева запустилась");
        Thread.Sleep(4000);
        wa = Screen.PrimaryScreen!.WorkingArea;
        Log($"рабочая область слева: {wa}");
        Check(wa.Left >= 40, "рабочая область сдвинулась вправо — панель задач слева");
        dummies = Enumerable.Range(1, 3).Select(i => { var p = Process.Start(self, $"--dummy {i}")!; Thread.Sleep(600); return p; }).ToList();
        Thread.Sleep(2500);
        Desktop();
        Shot("07_слева_Рыжий_кот");
        Strip("08_слева_крупно", true);
        var startL = Bar()?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Пуск"));
        Click(startL);
        var menuL = Wait(() => { var w = AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, "Свой Пуск")); return w != null && !w.Current.IsOffscreen ? w : null; }, 25);
        Thread.Sleep(2500);
        Check(menuL != null && menuL.Current.BoundingRectangle.Left < 120, "у левой панели «Свой Пуск» открывается справа от неё сверху");
        Shot("08_пуск_у_левой_панели");
        Desktop(); Thread.Sleep(800);

        // нагрузка в простое
        foreach (var p in dummies) { try { p.Kill(); } catch { } }
        Thread.Sleep(3000);
        var vb = Process.GetProcessesByName("Ven4Bar").FirstOrDefault();
        if (vb != null)
        {
            var c0 = vb.TotalProcessorTime; Thread.Sleep(10000); vb.Refresh();
            var cpu = (vb.TotalProcessorTime - c0).TotalMilliseconds / 10000.0 / Environment.ProcessorCount * 100;
            Log($"ЦП в простое: {cpu:0.00} %, память {vb.WorkingSet64 / 1048576} МБ");
            Check(cpu < 0.5, "в простое почти не грузит процессор");
        }
        SoftClose();
        Wait(() => Process.GetProcessesByName("Ven4Bar").Length == 0 ? "" : null, 10);
        Thread.Sleep(1500);
        Check(AutoHideState() == stateBefore, "после второй панели родная панель снова как была");

        // ── 3. Новые режимы: край, размер, вид, подписи, автоскрытие, меню, перетаскивание ──
        Theme("violet");
        var modes = new (string Json, string Name, Func<bool> Ok, string What)[]
        {
            ("{\"Edge\":1,\"Size\":1,\"Style\":\"glass\"}", "10_сверху_стекло", () => Screen.PrimaryScreen!.WorkingArea.Top >= 40, "панель сверху — рабочая область ниже"),
            ("{\"Edge\":2,\"Size\":2,\"Style\":\"solid\"}", "11_справа_большая", () => Screen.PrimaryScreen!.WorkingArea.Right <= Screen.PrimaryScreen.Bounds.Right - 50, "панель справа, большая"),
            ("{\"Edge\":3,\"Size\":1,\"Style\":\"island\",\"Labels\":true}", "12_остров_с_подписями", () => true, "остров посередине с подписями окон"),
            ("{\"Edge\":3,\"Size\":0,\"Style\":\"clear\"}", "13_прозрачная_маленькая", () => true, "прозрачная маленькая"),
        };
        var ds = Enumerable.Range(1, 3).Select(i => { var p = Process.Start(self, $"--dummy {i}")!; Thread.Sleep(500); return p; }).ToList();
        foreach (var (json, name, ok, what) in modes)
        {
            BarSet(json);
            Process.Start(new ProcessStartInfo(exe, "--hide-native") { UseShellExecute = false });
            var b2 = Wait(Bar, 30); Thread.Sleep(3500);
            Check(b2 != null && ok(), what);
            Desktop(); Shot(name);
            SoftClose(); Wait(() => Process.GetProcessesByName("Ven4Bar").Length == 0 ? "" : null, 10); Thread.Sleep(1200);
        }

        // автоскрытие у правого края
        BarSet("{\"Edge\":2,\"Size\":1,\"Style\":\"solid\",\"AutoHide\":true}");
        Process.Start(new ProcessStartInfo(exe, "--hide-native") { UseShellExecute = false });
        var ah = Wait(Bar, 30); Thread.Sleep(4500);
        var sbx = Screen.PrimaryScreen!.Bounds;
        var hr = ah?.Current.BoundingRectangle ?? System.Windows.Rect.Empty;
        Log($"спрятанная панель: {hr}");
        Check(ah != null && hr.Left >= sbx.Right - 6, "автоскрытие: панель ушла за правый край (видна полоска)");
        Check(Screen.PrimaryScreen.WorkingArea.Right == sbx.Right, "автоскрытие: место у края не занято");
        SetCursorPos(sbx.Right - 1, sbx.Height / 2); Thread.Sleep(300); SetCursorPos(sbx.Right - 1, sbx.Height / 2 + 5); Thread.Sleep(1500);
        hr = Bar()?.Current.BoundingRectangle ?? hr;
        Check(hr.Left <= sbx.Right - 40, "автоскрытие: панель выехала при наведении");
        Shot("14_автоскрытие_выехала");
        Desktop(); Thread.Sleep(2000);
        hr = Bar()?.Current.BoundingRectangle ?? hr;
        Check(hr.Left >= sbx.Right - 6, "автоскрытие: снова спряталась");
        SoftClose(); Wait(() => Process.GetProcessesByName("Ven4Bar").Length == 0 ? "" : null, 10); Thread.Sleep(1200);

        // меню правого щелчка и перетаскивание (снизу, обычная)
        BarSet("{\"Edge\":3,\"Size\":1,\"Style\":\"solid\"}");
        Process.Start(new ProcessStartInfo(exe, "--hide-native") { UseShellExecute = false });
        var bm = Wait(Bar, 30); Thread.Sleep(3500);
        var br = bm!.Current.BoundingRectangle;
        SetCursorPos((int)(br.Left + 200), (int)(br.Bottom - 20)); Thread.Sleep(200);
        mouse_event(0x8, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(60); mouse_event(0x10, 0, 0, 0, UIntPtr.Zero);
        var mi = Wait(() => AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem), new PropertyCondition(AutomationElement.NameProperty, "Диспетчер задач"))), 8);
        Check(mi != null, "правый щелчок: «Диспетчер задач», положение, размер, вид, подписи, автоскрытие");
        Shot("15_меню_правого_щелчка");
        System.Windows.Forms.SendKeys.SendWait("{ESC}"); Thread.Sleep(600);

        var target = Wait(() => Bar()!.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Окно 1")), 10);
        var srcp = Process.Start(self, "--dragsrc")!; Thread.Sleep(2500);
        if (target != null)
        {
            var tr = target.Current.BoundingRectangle;
            DragTo(150, 120, (int)(tr.Left + tr.Width / 2), (int)(tr.Top + tr.Height / 2));
            Thread.Sleep(1600);
            var fg = GetForegroundWindow();
            Shot("16_перетаскивание_окно_вышло");
            mouse_event(0x4, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(500);
            Check(fg == FindByTitle("Окно 1"), "перетаскивание: задержка над кнопкой выводит окно вперёд");
        }
        else Check(false, "перетаскивание: кнопка «Окно 1» не найдена");
        try { srcp.Kill(); } catch { }
        foreach (var p in ds) { try { p.Kill(); } catch { } }
        SoftClose(); Wait(() => Process.GetProcessesByName("Ven4Bar").Length == 0 ? "" : null, 10); Thread.Sleep(1500);
        Check(AutoHideState() == stateBefore, "после всех режимов родная панель как была");
        Check(Screen.PrimaryScreen!.WorkingArea.Left == 0, "рабочая область вернулась");
        Desktop(); Thread.Sleep(1500);
        Shot("09_после_выхода");
    }

    // ---------- общее ----------
    static T? Wait<T>(Func<T?> f, int sec) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < sec) { try { var r = f(); if (r != null) return r; } catch { } Thread.Sleep(300); }
        return null;
    }
    static void Invoke(AutomationElement? e)
    {
        if (e == null) return;
        try { ((InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); } catch (Exception ex) { Log("нажатие: " + ex.Message); }
    }
    [DllImport("user32.dll")] static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    /// <summary>Настоящий щелчок мышью (передний план Windows отдаёт только настоящему вводу).</summary>
    static void Click(AutomationElement? e)
    {
        if (e == null) return;
        var r = e.Current.BoundingRectangle;
        SetCursorPos((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2)); Thread.Sleep(150);
        mouse_event(0x2, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(60); mouse_event(0x4, 0, 0, 0, UIntPtr.Zero);
    }
    /// <summary>Мягкое закрытие — WM_CLOSE окну панели (как «Завершить задачу» без /F или выход из сеанса).</summary>
    static void SoftClose()
    {
        var b = Bar();
        if (b == null) { Log("закрытие: панель не найдена"); return; }
        PostMessage(new IntPtr(b.Current.NativeWindowHandle), 0x0010, IntPtr.Zero, IntPtr.Zero);
    }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    /// <summary>Перетаскивание настоящей мышью: нажать, вести короткими шагами, держать над целью.</summary>
    static void DragTo(int x0, int y0, int x1, int y1)
    {
        var b = Screen.PrimaryScreen!.Bounds;
        void Move(int x, int y) => mouse_event(0x8001, x * 65535 / (b.Width - 1), y * 65535 / (b.Height - 1), 0, UIntPtr.Zero);
        Move(x0, y0); Thread.Sleep(200);
        mouse_event(0x2, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(150);
        for (int i = 1; i <= 40; i++) { Move(x0 + (x1 - x0) * i / 40, y0 + (y1 - y0) * i / 40); Thread.Sleep(25); }
        for (int i = 0; i < 8; i++) { Move(x1 + (i % 2), y1); Thread.Sleep(120); }
    }
    static void Log(string s) { LogText.AppendLine($"{DateTime.Now:HH:mm:ss} {s}"); File.WriteAllText(Path.Combine(Out, "log.txt"), LogText.ToString(), Encoding.UTF8); }
    static void Check(bool ok, string what) { if (ok) Pass++; else Fail++; Log((ok ? "OK   " : "FAIL ") + what); }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint msg; public uint edge; public RECT rc; public IntPtr lParam; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string? cls, string title);
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int op);

    static int AutoHideState() { var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() }; return (int)SHAppBarMessage(4, ref d); }
    static IntPtr FindByTitle(string t) => FindWindowW(null, t);
    static void Desktop() { var b = Screen.PrimaryScreen!.Bounds; SetCursorPos(b.Width / 2, b.Height / 3); SetForegroundWindow(GetShellWindow()); Thread.Sleep(700); }

    static Bitmap Grab(Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height);
        using var g = Graphics.FromImage(bmp);
        var hdc = g.GetHdc(); var scr = GetDC(IntPtr.Zero);
        bool ok = BitBlt(hdc, 0, 0, r.Width, r.Height, scr, r.X, r.Y, 0x00CC0020 | 0x40000000);
        ReleaseDC(IntPtr.Zero, scr); g.ReleaseHdc(hdc);
        if (!ok) Log("BitBlt не прошёл: " + Marshal.GetLastPInvokeError());
        return bmp;
    }
    static void Shot(string name)
    {
        try { using var b = Grab(Screen.PrimaryScreen!.Bounds); b.Save(Path.Combine(Out, $"{++Shots:00}_{name}.png"), ImageFormat.Png); }
        catch (Exception ex) { Log("снимок не снят: " + ex.Message); }
    }
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);

    /// <summary>Ушки над «Пуском» — по пикселям: внутренняя часть ушка (#EBC3A8 у темы «Венчик») должна быть
    /// в прозрачной полосе над панелью именно над кнопкой «Пуск», а не где-то ещё.</summary>
    static void EarsCheck(AutomationElement bar)
    {
        var start = bar.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Пуск"));
        if (start == null) { Check(false, "ушки: кнопка «Пуск» не найдена"); return; }
        var br = bar.Current.BoundingRectangle; var sr = start.Current.BoundingRectangle;
        double k = GetDpiForWindow(new IntPtr(bar.Current.NativeWindowHandle)) / 96.0; if (k <= 0) k = 1;
        int top = (int)br.Top, h = Math.Max(4, (int)Math.Round(13 * k));
        int cx = (int)(sr.Left + sr.Width / 2), half = (int)Math.Max(sr.Width, 40 * k);
        using var b = Grab(new Rectangle((int)br.Left, top, (int)br.Width, h));
        int near = 0, far = 0, minX = int.MaxValue, maxX = int.MinValue;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
            {
                var c = b.GetPixel(x, y);
                if (Math.Abs(c.R - 0xEB) + Math.Abs(c.G - 0xC3) + Math.Abs(c.B - 0xA8) > 30) continue;
                int sx = x + (int)br.Left;
                if (Math.Abs(sx - cx) <= half) { near++; minX = Math.Min(minX, sx); maxX = Math.Max(maxX, sx); } else far++;
            }
        Log($"ушки: «Пуск» по центру x={cx}, пикселей ушка рядом {near} (x {minX}..{maxX}), в стороне {far}");
        // «в стороне» бывают и бежевые места обоев, поэтому мерило — ушко по обе стороны от центра «Пуска» и перевес рядом
        Check(near >= 20 && near > far * 2 && minX < cx - 4 && maxX > cx + 4, "ушки стоят над кнопкой «Пуск» (по пикселям)");
    }

    static void Strip(string name, bool left)
    {
        var s = Screen.PrimaryScreen!.Bounds;
        var r = left ? new Rectangle(0, 0, 120, s.Height) : new Rectangle(s.Width / 2 - 450, s.Height - 80, 900, 80);
        using var b = Grab(r); using var big = new Bitmap(b, b.Width * 2, b.Height * 2);
        big.Save(Path.Combine(Out, $"{++Shots:00}_{name}.png"), ImageFormat.Png);
    }
}
