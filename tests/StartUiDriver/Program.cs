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

namespace StartUiDriver;

// StartUiDriver.exe <папка отчёта> <путь к Ven4Start.exe>
internal static class Program
{
    static string Out = "";
    static readonly StringBuilder LogText = new();
    static int Pass, Fail, Shots;
    static readonly string ThemeFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "taskbar_theme.json");
    static readonly string SetFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Start", "settings.json");

    [STAThread]
    static int Main(string[] args)
    {
        Out = args[0]; var exe = args[1];
        Directory.CreateDirectory(Out);
        string? keep = File.Exists(ThemeFile) ? File.ReadAllText(ThemeFile) : null;
        // Настройки меню запоминаются здесь, а не посреди прохода: возврат в finally
        // срабатывает и тогда, когда проверка размеров упала исключением.
        string? keepSet = File.Exists(SetFile) ? File.ReadAllText(SetFile) : null;
        try { Run(exe); }
        catch (Exception ex) { Check(false, "исключение " + ex); Shot("ошибка"); }
        finally
        {
            // Сначала остановить меню: живое меню может переписать свои настройки после возврата.
            foreach (var p in Process.GetProcessesByName("Ven4Start")) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
            if (keep != null) File.WriteAllText(ThemeFile, keep); else File.Delete(ThemeFile);
            if (keepSet != null) File.WriteAllText(SetFile, keepSet); else if (File.Exists(SetFile)) File.Delete(SetFile);
        }
        Log($"ИТОГ: прошло {Pass}, не прошло {Fail}");
        Log("done");
        return Fail == 0 ? 0 : 1;
    }

    static void Theme(string id)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ThemeFile)!);
        File.WriteAllText(ThemeFile, "{\"ThemeId\":\"" + id + "\"}");
    }

    static AutomationElement? Menu() => AutomationElement.RootElement.FindFirst(TreeScope.Children,
        new PropertyCondition(AutomationElement.NameProperty, "Свой Пуск"));

    static AutomationElement? Show(string exe)
    {
        Process.Start(new ProcessStartInfo(exe, "--show") { UseShellExecute = false });
        var m = Wait(() => { var w = Menu(); return w != null && !w.Current.IsOffscreen ? w : null; }, 25);
        Thread.Sleep(2500);   // значки подгружаются в фоне
        return m;
    }

    static void Hide() { SetForegroundWindow(GetShellWindow()); Thread.Sleep(900); }

    static void Run(string exe)
    {
        Log($"Windows {Environment.OSVersion.Version}");
        Theme("venchik");
        var sw = Stopwatch.StartNew();
        var m = Show(exe);
        Check(m != null, $"меню открылось ({sw.ElapsedMilliseconds} мс с запуска)");
        if (m == null) { Shot("нет_меню"); return; }
        Shot("01_меню_Венчик");
        var tiles = m.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Count;
        Log($"кнопок в меню: {tiles}");
        Check(tiles >= 8, "закреплённые и кнопки питания на месте");
        var apps = m.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Программы"));
        var count = apps?.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count ?? 0;
        Check(count > 5, $"список программ заполнен ({count} видимых строк)");

        var search = m.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Поиск программ"));
        var face = m.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Венчик"));
        var fr = face?.Current.BoundingRectangle ?? System.Windows.Rect.Empty; var srr = search?.Current.BoundingRectangle ?? System.Windows.Rect.Empty;
        Check(face != null && fr.Width >= 20 && !srr.IsEmpty && fr.Right <= srr.Left + 2, "в шапке морда Венчика слева от поиска");
        if (search != null && search.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) ((ValuePattern)vp).SetValue("блок");
        Thread.Sleep(1200);
        Shot("02_поиск");
        var found = apps?.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count ?? 0;
        Check(found >= 1, $"поиск нашёл {found}");

        // подтверждение выключения — внутри окна, «Отмена» ничего не делает
        if (search != null && search.TryGetCurrentPattern(ValuePattern.Pattern, out vp)) ((ValuePattern)vp).SetValue("");
        var off = m.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Выключение"));
        Invoke(off); Thread.Sleep(800);
        Shot("03_подтверждение_выключения");
        var cancel = m.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.NameProperty, "Отмена")));
        Check(cancel != null, "подтверждение показано в окне");
        Invoke(cancel); Thread.Sleep(600);

        Hide();
        Check(Menu() == null || Menu()!.Current.IsOffscreen, "меню закрылось при потере фокуса");

        foreach (var (id, n) in new[] { ("ginger", "04_меню_Рыжий_кот"), ("light", "05_меню_Светлая"), ("sea", "06_меню_Глубокое_море") })
        {
            Theme(id);
            var mm = Show(exe);
            Check(mm != null, $"меню открылось в теме {id}");
            Shot(n);
            Hide();
        }

        // размеры и разделы (настройки меню)
        // Исходные настройки запомнены в Main и возвращаются там же в finally.
        Theme("venchik");
        Directory.CreateDirectory(Path.GetDirectoryName(SetFile)!);
        foreach (var (json, name) in new[] { ("{\"Size\":0}", "07_размер_компактный"), ("{\"Size\":2}", "08_размер_большой"), ("{\"Size\":1,\"ShowAll\":false}", "09_без_списка_программ") })
        {
            File.WriteAllText(SetFile, json);
            // меню перечитывает настройки при запуске — перезапускаем
            foreach (var pr in Process.GetProcessesByName("Ven4Start")) { try { pr.Kill(); pr.WaitForExit(3000); } catch { } }
            var ms = Show(exe);
            Check(ms != null, $"меню открылось: {name}");
            if (ms != null) Log($"{name}: окно {ms.Current.BoundingRectangle}");
            Shot(name);
            Hide();
        }
        var m2 = Show(exe);
        var gear = m2?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Настроить меню"));
        Invoke(gear); Thread.Sleep(900);
        Check(m2?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Компактный")) != null, "шестерёнка открывает настройки меню");
        Shot("10_настройки_меню");
        var big = m2?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Большой"));
        var w0 = m2?.Current.BoundingRectangle.Width ?? 0;
        Invoke(big); Thread.Sleep(900);
        Check((m2?.Current.BoundingRectangle.Width ?? 0) > w0 + 50, "«Большой» в настройках сразу увеличивает меню");
        Shot("11_большой_из_настроек");
        Hide();

        // нагрузка в простое: меню скрыто
        Thread.Sleep(3000);
        var p = Process.GetProcessesByName("Ven4Start").FirstOrDefault();
        Check(p != null, "одна копия работает в фоне");
        if (p != null)
        {
            var c0 = p.TotalProcessorTime; Thread.Sleep(10000); p.Refresh();
            var cpu = (p.TotalProcessorTime - c0).TotalMilliseconds / 10000.0 / Environment.ProcessorCount * 100;
            Log($"ЦП в простое: {cpu:0.00} %, память {p.WorkingSet64 / 1048576} МБ, копий {Process.GetProcessesByName("Ven4Start").Length}");
            Check(cpu < 0.5, "в простое почти не грузит процессор");
        }
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
    static void Log(string s) { LogText.AppendLine($"{DateTime.Now:HH:mm:ss} {s}"); File.WriteAllText(Path.Combine(Out, "log.txt"), LogText.ToString(), Encoding.UTF8); }
    static void Check(bool ok, string what) { if (ok) Pass++; else Fail++; Log((ok ? "OK   " : "FAIL ") + what); }

    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int op);

    static void Shot(string name)
    {
        try
        {
            var r = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            using var bmp = new Bitmap(r.Width, r.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc(); var scr = GetDC(IntPtr.Zero);
                bool ok = BitBlt(hdc, 0, 0, r.Width, r.Height, scr, 0, 0, 0x00CC0020 | 0x40000000);
                ReleaseDC(IntPtr.Zero, scr); g.ReleaseHdc(hdc);
                if (!ok) Log("BitBlt не прошёл: " + Marshal.GetLastPInvokeError());
            }
            bmp.Save(Path.Combine(Out, $"{++Shots:00}_{name}.png"), ImageFormat.Png);
        }
        catch (Exception ex) { Log("снимок не снят: " + ex.Message); }
    }
}
