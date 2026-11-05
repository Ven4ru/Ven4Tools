using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace Ven4Bar;

/// <summary>
/// Точка входа. Флаги:
///   --hide-native  включить автоскрытие родной панели задач (вернётся как было при выходе).
/// </summary>
internal static class Program
{
    private static BarWindow? _bar;
    private static WinForms.NotifyIcon? _tray;

    [STAThread]
    private static int Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\Ven4Bar.SingleInstance", out bool first);
        // Перезапуск после смены настроек: прежняя копия ещё закрывается — дождаться её.
        bool restarted = args.Any(a => a == "--restarted");
        if (!first && !(restarted && WaitMutex(mutex))) return 0;

        bool hideNative = args.Any(a => string.Equals(a, "--hide-native", StringComparison.OrdinalIgnoreCase));
        bool left = args.Any(a => string.Equals(a, "--left", StringComparison.OrdinalIgnoreCase));
        var settings = BarSettings.Load();
        if (left) settings.Edge = BarSettings.Left;   // --left — разовая подсказка, в настройки не пишется

        // Если прошлый запуск упал, не вернув автоскрытие, — возвращаем сейчас.
        NativeTaskbar.RestoreAfterCrash();

        AppDomain.CurrentDomain.ProcessExit += (_, _) => EmergencyCleanup();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { CrashLog.Write("необработанная ошибка", e.ExceptionObject as Exception); EmergencyCleanup(); };

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            CrashLog.Write("ошибка в потоке окна", e.Exception);
            EmergencyCleanup();
            MessageBox.Show("Ven4Bar завершается из-за ошибки:\n" + e.Exception.Message,
                "Ven4Bar", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
            app.Shutdown(1);
        };
        app.SessionEnding += (_, _) => EmergencyCleanup();
        app.Exit += (_, _) =>
        {
            _bar?.Cleanup();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        };

        _bar = new BarWindow(hideNative, settings);
        _bar.ExitRequested += () => app.Shutdown();
        _bar.RestartRequested += () =>
        {
            // новые настройки уже сохранены; --left не передаём — край теперь берётся из настроек
            var keep = args.Where(a => a != "--left" && a != "--restarted").Append("--restarted");
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, string.Join(" ", keep)) { UseShellExecute = false }); }
            catch (Exception ex) { CrashLog.Write("не удалось перезапустить панель с новыми настройками", ex); }
            app.Shutdown();
        };
        CreateTray(app);
        _bar.Show();

        return app.Run();
    }

    private static void CreateTray(Application app)
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Выход", null, (_, _) => app.Shutdown());
        _tray = new WinForms.NotifyIcon
        {
            Icon = TrayIcon(),
            Text = "Ven4Bar — своя панель задач",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    /// <summary>Значок в трее — голова Венчика в цвет темы.</summary>
    private static Icon TrayIcon()
    {
        var t = BarTheme.Current();
        var acc = BarTheme.C(t.Accent); var bar = BarTheme.C(t.Bar);
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var head = new SolidBrush(System.Drawing.Color.FromArgb(acc.R, acc.G, acc.B));
            using var face = new SolidBrush(System.Drawing.Color.FromArgb(bar.R, bar.G, bar.B));
            g.FillPolygon(head, new[] { new PointF(3, 15), new PointF(7, 2), new PointF(14, 10) });
            g.FillPolygon(head, new[] { new PointF(29, 15), new PointF(25, 2), new PointF(18, 10) });
            g.FillEllipse(head, 3, 7, 26, 23);
            g.FillEllipse(face, 9, 15, 4, 4); g.FillEllipse(face, 19, 15, 4, 4);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static bool WaitMutex(Mutex m)
    {
        try { return m.WaitOne(TimeSpan.FromSeconds(8)); }
        catch (AbandonedMutexException) { return true; }
    }

    private static void EmergencyCleanup()
    {
        try { _bar?.EmergencyCleanup(); } catch (Exception ex) { CrashLog.Write("аварийная уборка панели", ex); }
        try { NativeTaskbar.Restore(); } catch (Exception ex) { CrashLog.Write("возврат родной панели задач", ex); }
    }
}
