using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Ven4Tools.Launcher;

public partial class App : Application
{
    private static Mutex? _mutex;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        DispatcherUnhandledException += OnDispatcherException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "Ven4Tools.Launcher.SingleInstance", out bool createdNew);

        if (!createdNew)
        {
            MessageBox.Show(
                "Ven4Tools Launcher уже запущен.",
                "Уже запущен",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            _mutex.Dispose();
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown");
        WriteLauncherCrash(ex);
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        WriteLauncherCrash(e.Exception);
        // Shutdown after logging — continuing with corrupted UI state is unsafe
        Application.Current?.Shutdown(1);
    }

    private static void WriteLauncherCrash(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ven4Tools", "logs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"launcher_crash_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(file,
                $"[{DateTime.UtcNow:O}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        catch { }
    }
}
