using System.Threading;
using System.Windows;

namespace Ven4Tools.Launcher;

public partial class App : Application
{
    private static Mutex? _mutex;

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
}
