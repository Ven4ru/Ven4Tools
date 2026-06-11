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
            // Обнуляем поле: иначе OnExit вызовет ReleaseMutex() на уже освобождённом
            // мьютексе → ObjectDisposedException.
            _mutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_mutex != null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
            _mutex = null;
        }
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
        // Завершаем работу после записи лога — продолжать с повреждённым состоянием UI небезопасно
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
            // Миллисекунды + GUID исключают коллизию имён при двух крашах в одну секунду
            var file = Path.Combine(dir, $"launcher_crash_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.txt");
            File.WriteAllText(file,
                $"[{DateTime.UtcNow:O}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        catch { }
    }
}
