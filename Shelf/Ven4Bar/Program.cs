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
        if (!first) return 0;

        bool hideNative = args.Any(a => string.Equals(a, "--hide-native", StringComparison.OrdinalIgnoreCase));

        // Если прошлый запуск упал, не вернув автоскрытие, — возвращаем сейчас.
        NativeTaskbar.RestoreAfterCrash();

        AppDomain.CurrentDomain.ProcessExit += (_, _) => EmergencyCleanup();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => EmergencyCleanup();

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
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

        _bar = new BarWindow(hideNative);
        _bar.ExitRequested += () => app.Shutdown();
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
            Icon = SystemIcons.Application,
            Text = "Ven4Bar — своя панель задач",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    private static void EmergencyCleanup()
    {
        try { _bar?.EmergencyCleanup(); } catch { }
        try { NativeTaskbar.Restore(); } catch { }
    }
}
