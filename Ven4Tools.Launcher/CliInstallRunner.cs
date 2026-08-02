using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Launcher;

/// <summary>
/// Headless-путь для `Ven4Tools.Launcher.exe --install-from=<path> [--silent]` —
/// скриптовое/автоматизированное разворачивание клиента без открытия окна лаунчера.
/// Переиспользует MainWindow (без Show()) вместо дублирования логики установки —
/// InstallFromLocalArchiveAsync и ExtractAndInstallClientAsync общие с обычным UI-путём.
/// Вызывается ТОЛЬКО после того, как Dispatcher уже запущен (через Dispatcher.BeginInvoke
/// из App.OnStartup, см. Step 3) — синхронный вызов до старта цикла диспетчера
/// гарантированно вешает процесс на первом же Dispatcher.Invoke внутри
/// InstallFromLocalArchiveAsync (эмпирически воспроизведено при исполнении Task 7).
///
/// Известное ограничение (вне объёма этой задачи): конструктор MainWindow безусловно
/// создаёт иконку в трее и запускает фоновый апдейтер, даже когда окно не показывается —
/// при headless-запуске они могут кратковременно появиться/стартовать до Shutdown().
/// </summary>
internal static class CliInstallRunner
{
    public static async Task<int> RunAsync(MainWindow window, string archivePath, bool silent)
    {
        try
        {
            bool success = await window.InstallFromLocalArchiveAsync(
                archivePath, CancellationToken.None, silent);
            return success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ошибка: {ex.Message}");
            return 1;
        }
    }
}
