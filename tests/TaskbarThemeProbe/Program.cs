using System.IO;
using System.Windows.Forms;
using Application = System.Windows.Forms.Application;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;
using Rectangle = System.Drawing.Rectangle;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using Microsoft.Win32;

namespace TaskbarThemeProbe;

// Запуск в сеансе пользователя ВМ: TaskbarThemeProbe.exe <папка отчёта>
// Служебный режим: TaskbarThemeProbe.exe --dummy <N> — пустое окно (своя кнопка на панели).
internal static class Program
{
    static string Out = "";
    static readonly StringBuilder LogText = new();
    static int Pass, Fail;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--dummy") { Dummy(args[1]); return 0; }
        Out = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "TaskbarThemeProbe");
        Directory.CreateDirectory(Out);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var backupPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Lab-theme-backup.json");
        try
        {
            // Прошлый прогон упал посреди — сначала вернуть его исходное.
            if (File.Exists(backupPath))
            {
                Log("найден снимок прошлого прогона — возвращаю его");
                Reg.Restore(JsonSerializer.Deserialize<Dictionary<string, Reg.Val>>(File.ReadAllText(backupPath))!);
                Native.Broadcast();
                File.Move(backupPath, backupPath + ".used-" + DateTime.Now.ToString("HHmmss"), true);
            }
            // Сразу после входа рабочий стол ещё поднимается — ждём панель задач (до 2 минут).
            for (int w = 0; w < 120 && Native.TaskbarRect().Height == 0; w++) Thread.Sleep(1000);
            Thread.Sleep(5000);
            Log("панель задач: " + Native.TaskbarRect().ToString());
            var os = Environment.OSVersion.Version;
            Log($"Windows {os}, сборка {os.Build}, масштаб {Native.Dpi()}%");
            var original = Reg.Snapshot();
            File.WriteAllText(backupPath, JsonSerializer.Serialize(original));
            File.WriteAllText(Path.Combine(Out, "original.json"), JsonSerializer.Serialize(original, new JsonSerializerOptions { WriteIndented = true }));
            Log("исходно: " + string.Join("; ", original.Select(kv => $"{kv.Key}={kv.Value}")));
            Shot("00_исходно");

            try { Themes(original); }
            catch (Exception ex) { Check(false, "темы: исключение " + ex); }

            try { Ears(original); }
            catch (Exception ex) { Check(false, "ушки: исключение " + ex); }

            Reg.Restore(original);
            Native.Broadcast();
            Thread.Sleep(2500);
            var diff = Reg.Diff(original, Reg.Snapshot());
            Check(diff.Count == 0, "итоговый откат: реестр побайтно как был" + (diff.Count > 0 ? " — " + string.Join("; ", diff) : ""));
            Shot("99_итог_как_было");
            if (diff.Count == 0) File.Delete(backupPath);
        }
        catch (Exception ex) { Check(false, "исключение " + ex); }
        Log($"ИТОГ: прошло {Pass}, не прошло {Fail}");
        Log("done");
        return Fail == 0 ? 0 : 1;
    }

    // ---------------- темы ----------------
    record Theme(string Id, string Name, bool Dark, string Bar, string Acc, bool Prevalence, bool Transparency);
    static readonly Theme[] All =
    {
        new("venchik",  "Венчик ночной", true,  "#0F3B2C", "#52D18F", true,  true),
        new("graphite", "Графит",        true,  "#202020", "#60A5FA", false, true),
        new("sea",      "Глубокое море", true,  "#0D3A57", "#38BDF8", true,  true),
        new("violet",   "Ночной фиолет", true,  "#2C1F5C", "#A78BFA", true,  true),
        new("cherry",   "Вишня",         true,  "#5A1730", "#FB7185", true,  true),
        new("ginger",   "Рыжий кот",     true,  "#6A3410", "#FB923C", true,  true),
        new("light",    "Светлая",       false, "#EEF1F4", "#2563EB", false, true),
        new("midnight", "Полночь",       true,  "#0B1020", "#818CF8", true,  false),
    };

    static void Apply(Theme t)
    {
        var bar = Hex(t.Bar); var acc = Hex(t.Acc);
        // Палитра 8 оттенков, как у Windows: 0–2 светлее акцента, 3 — акцент, 4–6 — тёмные (заливка панели), 7 — серый.
        var pal = new[] { Mix(acc, Color.White, .5), Mix(acc, Color.White, .3), Mix(acc, Color.White, .15), acc,
                          Mix(bar, Color.White, .12), bar, Mix(bar, Color.Black, .2), Color.FromArgb(0x58, 0x58, 0x58) };
        var bytes = pal.SelectMany(c => new byte[] { c.R, c.G, c.B, 0 }).ToArray();
        Reg.SetBin(Reg.Accent, "AccentPalette", bytes);
        Reg.SetDw(Reg.Accent, "AccentColorMenu", Abgr(acc));
        Reg.SetDw(Reg.Accent, "StartColorMenu", Abgr(bar));
        Reg.SetDw(Reg.Dwm, "AccentColor", Abgr(acc));
        Reg.SetDw(Reg.Dwm, "ColorizationColor", unchecked((int)(0xC4000000u | (uint)(acc.R << 16 | acc.G << 8 | acc.B))));
        Reg.SetDw(Reg.Dwm, "ColorizationAfterglow", unchecked((int)(0xC4000000u | (uint)(acc.R << 16 | acc.G << 8 | acc.B))));
        Reg.SetDw(Reg.Personalize, "SystemUsesLightTheme", t.Dark ? 0 : 1);
        Reg.SetDw(Reg.Personalize, "ColorPrevalence", t.Prevalence ? 1 : 0);
        Reg.SetDw(Reg.Personalize, "EnableTransparency", t.Transparency ? 1 : 0);
        Native.Broadcast();
    }

    static void Themes(Dictionary<string, Reg.Val> original)
    {
        int i = 1;
        foreach (var t in All)
        {
            Apply(t);
            Thread.Sleep(3000);
            var px = TaskbarPixel();
            var dwm = Native.DwmColor();
            var expect = t.Prevalence ? Hex(t.Bar) : (Color?)null;
            var near = expect.HasValue ? Dist(px, expect.Value) : -1;
            Log($"тема «{t.Name}»: пиксель панели {Hx(px)}, ждали {(expect.HasValue ? Hx(expect.Value) : "системный")}" +
                $" (расстояние {near:0}), DwmGetColorizationColor {dwm}");
            if (t.Prevalence) Check(near >= 0 && near < 60, $"«{t.Name}»: панель окрасилась в цвет темы без перезапуска проводника");
            else Check(t.Dark ? Lum(px) < .2 : Lum(px) > .6, $"«{t.Name}»: панель {(t.Dark ? "тёмная" : "светлая")}");
            Shot($"{i++:00}_тема_{t.Id}");
            Reg.Restore(original);
            Native.Broadcast();
            Thread.Sleep(1200);
            var diff = Reg.Diff(original, Reg.Snapshot());
            Check(diff.Count == 0, $"«{t.Name}»: откат побайтно" + (diff.Count > 0 ? " — " + string.Join("; ", diff) : ""));
        }
    }

    static Color TaskbarPixel()
    {
        var r = Native.TaskbarRect();
        // пустое место панели: 12 % ширины от левого края (при значках по центру там пусто), середина по высоте
        int x = r.Left + (int)(r.Width * (Environment.OSVersion.Version.Build >= 22000 ? .12 : .62)), y = r.Top + r.Height / 2; // Win10: значки слева — меряем правее
        using var bmp = Native.Grab(new Rectangle(x, y, 1, 1));
        return bmp.GetPixel(0, 0);
    }

    // ---------------- ушки ----------------
    static void Ears(Dictionary<string, Reg.Val> original)
    {
        var t = All.First(x => x.Id == "ginger");
        Apply(t);
        Thread.Sleep(2500);
        // Окно панели — без имени класса: элемент UI Automation в точке на панели → вверх до окна верхнего уровня.
        var sw = Stopwatch.StartNew();
        var tr = Native.TaskbarRect();
        var hit = AutomationElement.FromPoint(new System.Windows.Point(tr.Left + 4, tr.Top + tr.Height / 2.0));
        var root = hit;
        for (var up = TreeWalker.ControlViewWalker.GetParent(root); up != null && up != AutomationElement.RootElement; up = TreeWalker.ControlViewWalker.GetParent(root)) root = up;
        Log($"окно панели через UI Automation: класс {root.Current.ClassName}, {sw.ElapsedMilliseconds} мс");
        Check(root != null && root.Current.NativeWindowHandle != 0, "окно панели найдено по точке (FromPoint), без имени класса");
        var start = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"));
        if (start == null)
        {
            // Windows 10: у «Пуска» нет AutomationId — это первая кнопка панели (всегда у левого края).
            start = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            if (start != null) Log($"«Пуск» найден запасным путём: первая кнопка панели «{start.Current.Name}», класс {start.Current.ClassName}");
        }
        Log($"поиск «Пуска» через UI Automation: {sw.ElapsedMilliseconds} мс");
        Check(start != null, "кнопка «Пуск» (AutomationId=StartButton) найдена");
        if (start == null) return;
        var r0 = start.Current.BoundingRectangle;
        Log($"«Пуск»: {r0}");

        EarsForm? form = null;
        var ready = new ManualResetEventSlim();
        var ui = new Thread(() =>
        {
            form = new EarsForm(Hex(t.Bar), Hex(t.Acc));
            form.Shown += (_, _) => ready.Set();
            Application.Run(form);
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.IsBackground = true;
        ui.Start();
        ready.Wait(10000);
        // Слежение: события окон SetWinEventHook (открылось/закрылось/показалось/скрылось) → через 250 мс
        // перечитать положение «Пуска». События UI Automation о сдвиге кнопки Windows 11 не присылает (проверено 04.11).
        form!.Invoke(() => form.Track(start, Log));
        var earsHwnd = form.Invoke(() => form.Handle);
        Thread.Sleep(1500);
        Shot("20_ушки_исходно", strip: true);
        var e0 = Native.WindowRect(earsHwnd);
        Check(Math.Abs((e0.Left + e0.Width / 2) - (r0.Left + r0.Width / 2)) <= 2 && e0.Bottom >= r0.Top - 8 && e0.Bottom <= r0.Top + 14,
              $"ушки стоят над «Пуском» (окно {e0}, кнопка {r0})");

        // 6 окон — у каждого своя кнопка; при значках по центру «Пуск» уезжает влево.
        var dummies = new List<Process>();
        var self = Environment.ProcessPath!;
        for (int i = 1; i <= 6; i++) { dummies.Add(Process.Start(self, $"--dummy {i}")!); Thread.Sleep(700); }
        Thread.Sleep(2500);
        var r1 = start.Current.BoundingRectangle;
        var e1 = Native.WindowRect(earsHwnd);
        Log($"после 6 окон: «Пуск» {r1}, ушки {e1}, пересчётов {form.Moves}");
        Shot("21_ушки_6_окон", strip: true);
        bool centered = Math.Abs(r1.Left - r0.Left) > 4;
        if (centered) Check(Math.Abs((e1.Left + e1.Width / 2) - (r1.Left + r1.Width / 2)) <= 2, "ушки уехали вместе с «Пуском»");
        else Log("«Пуск» не сдвинулся (значки слева или Windows 10) — проверка движения не нужна");

        foreach (var p in dummies) { try { p.Kill(); } catch { } }
        Thread.Sleep(3000);
        var r2 = start.Current.BoundingRectangle;
        var e2 = Native.WindowRect(earsHwnd);
        Log($"окна закрыты: «Пуск» {r2}, ушки {e2}, пересчётов {form.Moves}");
        Check(Math.Abs((e2.Left + e2.Width / 2) - (r2.Left + r2.Width / 2)) <= 2, "ушки вернулись вместе с «Пуском»");
        Shot("22_ушки_окна_закрыты", strip: true);

        // нагрузка окна ушек в простое (10 с)
        var me = Process.GetCurrentProcess();
        Thread.Sleep(3000); // дать улечься хвосту перечитываний после закрытия окон
        int ev0 = form.Events, mv0 = form.Moves;
        var cpu0 = me.TotalProcessorTime; Thread.Sleep(10000); me.Refresh();
        var cpu = (me.TotalProcessorTime - cpu0).TotalMilliseconds / 10000.0 / Environment.ProcessorCount * 100;
        Log($"ЦП в простое с ушками: {cpu:0.00} %, память {me.WorkingSet64 / 1048576} МБ, событий окон за 10 с {form.Events - ev0}, перерисовок {form.Moves - mv0}");
        Check(cpu < 0.5, "ушки в простое почти не грузят процессор");

        Automation.RemoveAllEventHandlers();
        form.BeginInvoke(form.Close);
        Reg.Restore(original);
        Native.Broadcast();
    }

    static void Dummy(string n)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var f = new Form { Text = "Окно " + n, Width = 360, Height = 160, StartPosition = FormStartPosition.Manual, Left = 80 + int.Parse(n) * 30, Top = 80 + int.Parse(n) * 30 };
        Application.Run(f);
    }

    // ---------------- общее ----------------
    static void Log(string s)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {s}";
        lock (LogText) { LogText.AppendLine(line); File.WriteAllText(Path.Combine(Out, "log.txt"), LogText.ToString(), Encoding.UTF8); }
    }
    static void Check(bool ok, string what) { if (ok) Pass++; else Fail++; Log((ok ? "OK   " : "FAIL ") + what); }

    static void Shot(string name, bool strip = false)
    {
        Thread.Sleep(800);
        try
        {
            var b = Screen.PrimaryScreen!.Bounds;
            var area = strip ? new Rectangle(b.Left, b.Bottom - Math.Max(140, b.Height / 7), b.Width, Math.Max(140, b.Height / 7)) : b;
            using var bmp = Native.Grab(area);
            bmp.Save(Path.Combine(Out, name + ".png"), ImageFormat.Png);
        }
        catch (Exception ex) { Log("снимок не снят: " + ex.Message); }
    }

    static Color Hex(string h) => ColorTranslator.FromHtml(h);
    static string Hx(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    static int Abgr(Color c) => unchecked((int)(0xFF000000u | (uint)(c.B << 16 | c.G << 8 | c.R)));
    static Color Mix(Color a, Color b, double t) => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    static double Dist(Color a, Color b) => Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));
    static double Lum(Color c) => (.2126 * c.R + .7152 * c.G + .0722 * c.B) / 255;
}

// Окно ушек: многослойное с попиксельной прозрачностью, клики насквозь, без фокуса, нет в Alt+Tab.
internal sealed class EarsForm : Form
{
    readonly Color _outer, _inner;
    public EarsForm(Color outer, Color inner)
    {
        _outer = outer; _inner = inner;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; Size = new Size(46, 13);
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
    public int Moves, Events;
    readonly HashSet<IntPtr> _seen = new();
    AutomationElement? _start; Action<string>? _log; System.Windows.Rect _last = System.Windows.Rect.Empty;
    System.Windows.Forms.Timer? _debounce; Native.WinEventProc? _proc; IntPtr _hook;
    public void Track(AutomationElement start, Action<string> log)
    {
        _start = start; _log = log;
        // После события — три перечитывания (0,25 / 0,8 / 1,6 с): значки на панели сдвигаются с анимацией.
        int step = 0; int[] delays = { 250, 550, 800 };
        _debounce = new System.Windows.Forms.Timer { Interval = delays[0] };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop(); Recheck();
            if (++step < delays.Length) { _debounce.Interval = delays[step]; _debounce.Start(); }
        };
        _proc = (h, ev, hwnd, idObject, idChild, th, time) =>
        {
            if (idObject != 0 || idChild != 0 || hwnd == Handle) return;   // только окна целиком, не своё
            if (ev == 0x8001 /*DESTROY*/) { if (!_seen.Remove(hwnd)) return; }  // уничтожено окно, которое мы видели
            else
            {
                if (Native.GetAncestor(hwnd, 2 /*GA_ROOT*/) != hwnd) return;  // только окна верхнего уровня
                if (ev == 0x8002) _seen.Add(hwnd); else _seen.Remove(hwnd);
            }
            Events++;
            step = 0; _debounce.Interval = delays[0]; _debounce.Stop(); _debounce.Start();
        };
        _hook = Native.SetWinEventHook(0x8001 /*DESTROY*/, 0x8003 /*HIDE*/, IntPtr.Zero, _proc, 0, 0, 0 /*OUTOFCONTEXT*/);
        _log($"слежение за окнами: {(_hook != IntPtr.Zero ? "включено" : "НЕ включилось")}");
        Recheck();
    }
    int _busy;
    void Recheck()
    {
        // UI Automation — в фоновом потоке (вызов из потока окна зависал), рисование — в потоке окна.
        if (Interlocked.Exchange(ref _busy, 1) == 1) { _debounce!.Start(); return; }
        Task.Run(() =>
        {
            try
            {
                var r = _start!.Current.BoundingRectangle;
                if (r != _last) BeginInvoke(() => { try { _last = r; Moves++; PlaceOver(r); } catch (Exception ex) { _log?.Invoke("ушки: ошибка рисования — " + ex.Message); } });
            }
            catch (Exception ex) { _log?.Invoke("ушки: ошибка чтения «Пуска» — " + ex.Message); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }
    public Rectangle Real() => Native.WindowRect(Handle);
    protected override void OnFormClosed(FormClosedEventArgs e) { if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook); base.OnFormClosed(e); }
    public void PlaceOver(System.Windows.Rect start)
    {
        // размер ушек — от ширины кнопки (масштаб экрана учитывается сам)
        int w = (int)Math.Round(start.Width * 1.28), h = (int)Math.Round(w * 13.0 / 46);
        int x = (int)Math.Round(start.Left + start.Width / 2 - w / 2.0);
        int y = (int)Math.Round(start.Top) - h + (int)Math.Round(start.Height * .10);
        var tb = Native.TaskbarRect();
        y = Math.Min(y, tb.Top - h + 2); // ушки стоят на верхнем крае панели
        Draw(w, h, x, y);
    }
    void Draw(int w, int h, int x, int y)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
            g.ScaleTransform(w / 62f, h / 15f);
            using var o = new SolidBrush(_outer); using var i = new SolidBrush(Color.FromArgb(217, _inner));
            g.FillPath(o, Ear(1, 15.5f, 12.5f, 22.5f, false)); g.FillPath(o, Ear(39.5f, 15.5f, 49.5f, 61, true));
            g.FillPath(i, Ear(8, 15.5f, 15.6f, 19.5f, false)); g.FillPath(i, Ear(42.5f, 15.5f, 46.4f, 54, true));
        }
        Native.UpdateLayered(Handle, bmp, x, y);
        Bounds = new Rectangle(x, y, w, h);
    }
    // скруглённый треугольник: основание [x0..x1] на y, вершина над tipX
    static GraphicsPath Ear(float x0, float y, float tipX, float x1, bool right)
    {
        var p = new GraphicsPath();
        var top = y - (x1 - x0) * (right ? .66f : .66f);
        top = Math.Max(top, .8f);
        p.AddBezier(x0, y, x0 + (tipX - x0) * .45f, y - (y - top) * .55f, tipX - 1.6f, top, tipX, top);
        p.AddBezier(tipX, top, tipX + 1.6f, top, x1 - (x1 - tipX) * .35f, top + (y - top) * .25f, x1, y);
        p.CloseFigure();
        return p;
    }
}

internal static class Reg
{
    public const string Accent = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    public const string Dwm = @"Software\Microsoft\Windows\DWM";
    public const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    static readonly (string Key, string Name)[] Values =
    {
        (Accent, "AccentPalette"), (Accent, "AccentColorMenu"), (Accent, "StartColorMenu"),
        (Dwm, "AccentColor"), (Dwm, "ColorizationColor"), (Dwm, "ColorizationAfterglow"),
        (Personalize, "SystemUsesLightTheme"), (Personalize, "ColorPrevalence"), (Personalize, "EnableTransparency"),
    };
    public sealed record Val(bool Exists, string Kind, string Data)
    {
        public override string ToString() => Exists ? $"{Kind}:{Data}" : "<нет>";
    }
    public static Dictionary<string, Val> Snapshot()
    {
        var d = new Dictionary<string, Val>();
        foreach (var (k, n) in Values)
        {
            using var key = Registry.CurrentUser.OpenSubKey(k);
            var v = key?.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            d[k + "\\" + n] = v == null ? new Val(false, "", "") :
                new Val(true, key!.GetValueKind(n).ToString(), v is byte[] b ? Convert.ToHexString(b) : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!);
        }
        return d;
    }
    public static void Restore(Dictionary<string, Val> snap)
    {
        foreach (var (path, v) in snap)
        {
            var i = path.LastIndexOf('\\'); var k = path[..i]; var n = path[(i + 1)..];
            using var key = Registry.CurrentUser.CreateSubKey(k);
            if (!v.Exists) { key.DeleteValue(n, false); continue; }
            var kind = Enum.Parse<RegistryValueKind>(v.Kind);
            object data = kind switch
            {
                RegistryValueKind.Binary => Convert.FromHexString(v.Data),
                RegistryValueKind.DWord => unchecked((int)long.Parse(v.Data)),
                RegistryValueKind.QWord => long.Parse(v.Data),
                _ => v.Data,
            };
            key.SetValue(n, data, kind);
        }
    }
    public static List<string> Diff(Dictionary<string, Val> a, Dictionary<string, Val> b) =>
        a.Where(kv => b[kv.Key] != kv.Value).Select(kv => $"{kv.Key}: было {kv.Value}, стало {b[kv.Key]}").ToList();
    public static void SetDw(string k, string n, int v) { using var key = Registry.CurrentUser.CreateSubKey(k); key.SetValue(n, v, RegistryValueKind.DWord); }
    public static void SetBin(string k, string n, byte[] v) { using var key = Registry.CurrentUser.CreateSubKey(k); key.SetValue(n, v, RegistryValueKind.Binary); }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint msg; public uint edge; public RECT rc; public IntPtr lParam; }
    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, uint m, UIntPtr w, string l, uint f, uint t, out UIntPtr r);
    [DllImport("dwmapi.dll")] static extern int DwmGetColorizationColor(out uint c, out bool opaque);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pt, ref SIZE sz, IntPtr src, ref POINT sp, int key, ref BLEND b, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);

    static APPBARDATA Bar() { var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() }; SHAppBarMessage(5 /*ABM_GETTASKBARPOS*/, ref d); return d; }
    public static IntPtr TaskbarHwnd() => Bar().hWnd;
    public static Rectangle TaskbarRect() { var r = Bar().rc; return Rectangle.FromLTRB(r.L, r.T, r.R, r.B); }
    public static int Dpi() => (int)(GetDpiForSystem() * 100 / 96);
    public static string DwmColor() => DwmGetColorizationColor(out var c, out var o) == 0 ? $"#{c:X8}{(o ? " непрозр." : "")}" : "ошибка";
    public static void Broadcast()
    {
        SendMessageTimeout((IntPtr)0xFFFF, 0x001A, UIntPtr.Zero, "ImmersiveColorSet", 0x2, 3000, out _);
        SendMessageTimeout((IntPtr)0xFFFF, 0x001A, UIntPtr.Zero, "WindowsThemeElement", 0x2, 3000, out _);
    }
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    public static Rectangle WindowRect(IntPtr h) { GetWindowRect(h, out var r); return Rectangle.FromLTRB(r.L, r.T, r.R, r.B); }
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int op);
    // снимок экрана вместе с многослойными окнами (CAPTUREBLT), иначе ушки на снимке не видны
    public static Bitmap Grab(Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height);
        using var g = Graphics.FromImage(bmp);
        var hdc = g.GetHdc(); var scr = GetDC(IntPtr.Zero);
        try { if (!BitBlt(hdc, 0, 0, r.Width, r.Height, scr, r.X, r.Y, 0x00CC0020 | 0x40000000)) throw new System.ComponentModel.Win32Exception(); }
        finally { ReleaseDC(IntPtr.Zero, scr); g.ReleaseHdc(hdc); }
        return bmp;
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
