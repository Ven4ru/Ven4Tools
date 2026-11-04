using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using Microsoft.Win32;

namespace TaskbarUiDriver;

// TaskbarUiDriver.exe <папка отчёта> <путь к Ven4Tools.exe>
internal static class Program
{
    static string Out = "";
    static readonly StringBuilder LogText = new();
    static int Pass, Fail, Shots;
    static bool Win11 => Environment.OSVersion.Version.Build >= 22000;

    [STAThread]
    static int Main(string[] args)
    {
        Out = args[0]; var exe = args[1];
        Directory.CreateDirectory(Out);
        try { Run(exe); }
        catch (Exception ex) { Check(false, "исключение " + ex); Shot("ошибка"); }
        Log($"ИТОГ: прошло {Pass}, не прошло {Fail}");
        Log("done");
        return Fail == 0 ? 0 : 1;
    }

    static readonly (string Key, string Name)[] Watched =
    {
        (@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentPalette"),
        (@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentColorMenu"),
        (@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "StartColorMenu"),
        (@"Software\Microsoft\Windows\DWM", "AccentColor"),
        (@"Software\Microsoft\Windows\DWM", "ColorizationColor"),
        (@"Software\Microsoft\Windows\DWM", "ColorizationAfterglow"),
        (@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme"),
        (@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "ColorPrevalence"),
        (@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency"),
        (@"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3", "Settings"),
        (@"Software\Microsoft\Windows\CurrentVersion\Run", "Ven4ToolsEars"),
        (@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAl"),
        (@"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode"),
        (@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled"),
        (@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions"),
        (@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled"),
        (@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338387Enabled"),
        (@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_IrisRecommendations"),
        (@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton"),
    };

    static string RegDump()
    {
        var sb = new StringBuilder();
        foreach (var (k, n) in Watched)
        {
            using var key = Registry.CurrentUser.OpenSubKey(k);
            var v = key?.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            sb.Append(n).Append('=').Append(v == null ? "<нет>" : v is byte[] b ? Convert.ToHexString(b) : v.ToString()).Append("; ");
        }
        return sb.ToString();
    }

    static void Run(string exe)
    {
        Log($"Windows {Environment.OSVersion.Version}, Windows 11: {Win11}");
        // Без профиля клиент сначала открывает «Добро пожаловать» — создаём профиль с полным каталогом.
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "profile.json");
        if (!File.Exists(profile)) { Directory.CreateDirectory(Path.GetDirectoryName(profile)!); File.WriteAllText(profile, "{\"CatalogMode\":\"full\",\"HasSelectedCategory\":true}"); }
        var before = RegDump();
        Log("реестр до: " + before);
        Shot("00_рабочий_стол_до");

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
        var win = Wait(() => AutomationElement.RootElement.FindFirst(TreeScope.Children,
            new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                             new PropertyCondition(AutomationElement.NameProperty, "Ven4Tools"))), 120);
        Check(win != null, "клиент открылся");
        if (win == null) return;
        Thread.Sleep(4000);
        try { ((WindowPattern)win.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Maximized); } catch { }
        Thread.Sleep(1500);

        var nav = Find(win, "Персонализация", ControlType.Button);
        Check(nav != null, "кнопка «Персонализация» найдена");
        Invoke(nav); Thread.Sleep(3000);

        var section = Wait(() => win.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "preview")), 20);
        Check(section != null, "витрина панели задач есть во вкладке");
        ScrollTo(win, section);
        Shot("01_раздел_исходно", win);

        foreach (var name in new[] { "Венчик", "Рыжий кот", "Глубокое море", "Светлая" })
        {
            var b = Find(win, name, ControlType.Button);
            Check(b != null, $"тема «{name}» есть");
            Invoke(b); Thread.Sleep(900);
            Shot($"02_выбрана_{name}", win);
        }

        var presets = win.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "lstPresets"));
        ScrollTo(win, presets);
        Shot("02_темы_и_наборы", win);
        var preset = Find(win, Win11 ? "Как в Windows 10" : "Чистая панель", ControlType.Button);
        Check(preset != null, "набор есть");
        Invoke(preset); Thread.Sleep(900);
        Shot("03_набор_на_витрине", win);
        Invoke(preset); Thread.Sleep(500);   // снять выбор

        // Применить «Венчик» с ушками
        Invoke(Find(win, "Венчик", ControlType.Button)); Thread.Sleep(600);
        var apply = win.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "btnTaskbarApply"));
        Check(apply != null, "кнопка «Применить» раздела найдена");
        Invoke(apply);
        Thread.Sleep(7000);
        Dialogs("Нет");
        Shot("04_применено_Венчик_окно", win);
        var mid = RegDump();
        Check(mid != before, "тема применилась (реестр изменился)");
        Check(Process.GetProcessesByName("VenchikEars").Length == 1, "ушки запущены (одна копия)");
        Minimize(win);
        var sb0 = System.Windows.Forms.Screen.PrimaryScreen!.Bounds; SetCursorPos(sb0.Width / 2, sb0.Height / 3);
        Thread.Sleep(3500);
        Shot("05_панель_с_ушками_Венчик");
        Strip("05_панель_крупно");
        Restore(win);

        // Другая тема — ушки перекрашиваются
        Invoke(Find(win, "Ночной фиолет", ControlType.Button)); Thread.Sleep(600);
        Invoke(apply); Thread.Sleep(6000); Dialogs("Нет");
        Check(Process.GetProcessesByName("VenchikEars").Length == 1, "после смены темы ушки одной копией");
        Minimize(win); Thread.Sleep(3500);
        Shot("06_панель_Ночной_фиолет");
        Strip("06_панель_крупно");
        Restore(win);

        // Тонкая настройка
        var exp = win.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "expFine"));
        if (exp != null && exp.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ec)) { ((ExpandCollapsePattern)ec).Expand(); Thread.Sleep(1200); ScrollTo(win, exp); }
        Check(exp != null, "«Тонкая настройка» раскрывается");
        Shot("07_тонкая_настройка", win);

        // Набор «Без рекламы и Bing»
        var presetsL = win.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "lstPresets"));
        ScrollTo(win, presetsL);
        var adFree = Find(win, "Без рекламы и Bing", ControlType.Button);
        Check(adFree != null, "набор «Без рекламы и Bing» есть");
        Invoke(adFree); Thread.Sleep(700);
        Shot("07_набор_без_рекламы", win);
        Invoke(apply); Thread.Sleep(5000);
        Dialogs("Нет");
        int? Dw(string k, string n) { using var key = Registry.CurrentUser.OpenSubKey(k); return key?.GetValue(n) as int?; }
        Check(Dw(@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled") == 0, "поиск Пуска — только по компьютеру (Bing выключен)");
        Check(Dw(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions") == 1, "веб-подсказки в поиске выключены политикой");
        Check(Dw(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled") == 0, "предложения программ в Пуске выключены");
        Check(Dw(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338387Enabled") == 0, "реклама на экране блокировки выключена");

        if (!Win11)
        {
            // Windows 10: панель сверху (слева проверено 04.11 днём)
            ScrollTo(win, section);
            var cmb = win.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "cmbEdge"));
            if (cmb != null && cmb.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ecp)) { ((ExpandCollapsePattern)ecp).Expand(); Thread.Sleep(500); }
            var top = cmb?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Сверху"));
            if (top != null && top.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sip)) ((SelectionItemPattern)sip).Select();
            if (cmb != null && cmb.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out ecp)) ((ExpandCollapsePattern)ecp).Collapse();
            Thread.Sleep(800);
            Shot("08_витрина_панель_сверху", win);
            Invoke(apply); Thread.Sleep(5000);
            Dialogs("Да");
            Thread.Sleep(9000);
            win = Wait(() => AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, "Ven4Tools")), 20) ?? win;
            Minimize(win); Thread.Sleep(3000);
            Shot("09_панель_сверху_настоящая");
            Check(TaskbarEdge() == 1, "панель задач встала сверху");
            Restore(win);
        }
        else
        {
            var cmb = win.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "cmbEdge"));
            Check(cmb != null && !cmb.Current.IsEnabled, "в Windows 11 выбор края родной панели неактивен с пояснением (панель у края — в Ven4Bar)");
        }

        // Откат
        ScrollTo(win, section);
        var revert = win.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "btnTaskbarRevert"));
        Invoke(revert); Thread.Sleep(5000);
        Dialogs("Да");
        Thread.Sleep(Win11 ? 3000 : 10000);
        var after = RegDump();
        Check(after == before, "«Вернуть как было» вернул реестр побайтно" + (after == before ? "" : " — после: " + after));
        Check(Process.GetProcessesByName("VenchikEars").Length == 0, "ушки закрыты после отката");
        win = AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, "Ven4Tools")) ?? win;
        Shot("10_после_отката_окно", win);
        Minimize(win); Thread.Sleep(2500);
        Shot("11_рабочий_стол_после_отката");
        Check(Win11 || TaskbarEdge() == 3, "панель снова внизу");

        foreach (var p in Process.GetProcessesByName("Ven4Tools")) { try { p.CloseMainWindow(); p.WaitForExit(5000); if (!p.HasExited) p.Kill(); } catch { } }
    }

    // ---------- UI Automation ----------
    static T? Wait<T>(Func<T?> f, int sec) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < sec) { try { var r = f(); if (r != null) return r; } catch { } Thread.Sleep(500); }
        return null;
    }

    static AutomationElement? Find(AutomationElement root, string name, ControlType type) =>
        Wait(() => root.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, name), new PropertyCondition(AutomationElement.ControlTypeProperty, type))), 8);

    static void Invoke(AutomationElement? e)
    {
        if (e == null) return;
        try { if (e.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var si)) ((ScrollItemPattern)si).ScrollIntoView(); } catch { }
        try { ((InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); }
        catch (Exception ex) { Log("нажатие не прошло: " + ex.Message); }
    }

    static void ScrollTo(AutomationElement win, AutomationElement? target)
    {
        if (target == null) return;
        // прокручиваемая область, внутри которой лежит элемент (самая маленькая из подходящих)
        var t0 = target.Current.BoundingRectangle;
        var sv = win.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true)).Cast<AutomationElement>()
            .Where(e => { try { var r = e.Current.BoundingRectangle; return ((ScrollPattern)e.GetCurrentPattern(ScrollPattern.Pattern)).Current.VerticallyScrollable && r.Left <= t0.Left + 5 && r.Right >= t0.Left + 5; } catch { return false; } })
            .OrderBy(e => e.Current.BoundingRectangle.Width * e.Current.BoundingRectangle.Height).FirstOrDefault();
        if (sv == null || !sv.TryGetCurrentPattern(ScrollPattern.Pattern, out var sp)) { Log("прокрутка: область не найдена"); return; }
        var scroll = (ScrollPattern)sp;
        var wr = win.Current.BoundingRectangle;
        for (int i = 0; i < 60; i++)
        {
            var r = target.Current.BoundingRectangle;
            if (r.IsEmpty) break;
            if (r.Top < wr.Top + 120) { try { scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.SmallDecrement); } catch { break; } }
            else if (r.Top > wr.Top + 200) { try { scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.SmallIncrement); } catch { break; } }
            else break;
            Thread.Sleep(60);
        }
        Thread.Sleep(400);
    }

    /// <summary>Закрыть диалоги клиента («Перезапустить проводник?» и т.п.) кнопкой с этим текстом.</summary>
    static void Dialogs(string answer)
    {
        for (int i = 0; i < 6; i++)
        {
            var dlg = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                new PropertyCondition(AutomationElement.NameProperty, "Панель задач и Пуск")));
            if (dlg == null) { Thread.Sleep(700); continue; }
            Log("диалог: " + string.Join(" | ", dlg.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>().Select(t => t.Current.Name)));
            Shot("диалог", dlg);
            var btn = dlg.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.NameProperty, answer)))
                   ?? dlg.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.NameProperty, "ОК")));
            Invoke(btn); Thread.Sleep(1500);
        }
    }

    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    static void Minimize(AutomationElement w)
    {
        try { ((WindowPattern)w.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Minimized); } catch { }
        // фокус — рабочему столу, иначе он уходит на «Пуск» и всплывает подсказка поверх ушек
        Thread.Sleep(600); SetForegroundWindow(GetShellWindow());
    }
    static void Restore(AutomationElement w) { try { ((WindowPattern)w.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Maximized); } catch { } Thread.Sleep(1500); }

    // ---------- снимки ----------
    static void Log(string s)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {s}";
        LogText.AppendLine(line); File.WriteAllText(Path.Combine(Out, "log.txt"), LogText.ToString(), Encoding.UTF8);
    }
    static void Check(bool ok, string what) { if (ok) Pass++; else Fail++; Log((ok ? "OK   " : "FAIL ") + what); }

    static void Shot(string name, AutomationElement? el = null)
    {
        try
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            Rectangle area = b;
            if (el != null) { var r = el.Current.BoundingRectangle; if (!r.IsEmpty) area = Rectangle.Intersect(b, new Rectangle((int)r.Left, (int)r.Top, (int)r.Width, (int)r.Height)); }
            using var bmp = Grab(area);
            bmp.Save(Path.Combine(Out, $"{++Shots:00}_{name}.png"), ImageFormat.Png);
        }
        catch (Exception ex) { Log("снимок не снят: " + ex.Message); }
    }

    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    static void Strip(string name)
    {
        var sb = System.Windows.Forms.Screen.PrimaryScreen!.Bounds; SetCursorPos(sb.Width / 2, sb.Height / 3); Thread.Sleep(1200);
        var tb = TaskbarRect(out _);
        var area = new Rectangle(tb.Left, Math.Max(0, tb.Top - 40), Math.Min(tb.Width, 900), tb.Height + 40);
        if (Win11) { var cx = tb.Left + tb.Width / 2; area.X = Math.Max(0, cx - 450); }
        using var bmp = Grab(area);
        using var big = new Bitmap(bmp, bmp.Width * 2, bmp.Height * 2);
        big.Save(Path.Combine(Out, $"{++Shots:00}_{name}.png"), ImageFormat.Png);
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint msg; public uint edge; public RECT rc; public IntPtr lParam; }
    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int op);

    static Rectangle TaskbarRect(out uint edge)
    {
        var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        SHAppBarMessage(5, ref d); edge = d.edge;
        return Rectangle.FromLTRB(d.rc.L, d.rc.T, d.rc.R, d.rc.B);
    }
    static uint TaskbarEdge() { TaskbarRect(out var e); return e; }

    static Bitmap Grab(Rectangle r)
    {
        var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height));
        using var g = Graphics.FromImage(bmp);
        var hdc = g.GetHdc(); var scr = GetDC(IntPtr.Zero);
        bool ok;
        try { ok = BitBlt(hdc, 0, 0, r.Width, r.Height, scr, r.X, r.Y, 0x00CC0020 | 0x40000000); }
        finally { ReleaseDC(IntPtr.Zero, scr); g.ReleaseHdc(hdc); }
        if (!ok)
        {
            Log($"BitBlt не прошёл (ошибка {Marshal.GetLastPInvokeError()}), снимаю через CopyFromScreen");
            g.CopyFromScreen(r.Location, Point.Empty, r.Size);
        }
        return bmp;
    }
}
