using System;
using System.Threading.Tasks;

namespace Ven4Tools.Launcher;

/// <summary>
/// Headless-путь для
/// `Ven4Tools.Launcher.exe --install-from=<path> [--silent] [--allow-downgrade]` —
/// скриптовое/автоматизированное разворачивание клиента без открытия окна лаунчера.
///
/// Ключи:
///   --install-from=<path>  подписанный архив клиента, который нужно установить;
///   --silent               без диалогов и сообщений;
///   --allow-downgrade      установить архив, даже если его версия старее
///                          установленной или текущей опубликованной. Без ключа такой
///                          архив не ставится: старые сборки не получают исправлений,
///                          а спросить подтверждение в этом режиме не у кого.
///
/// Коды возврата: 0 — установлено; 1 — ошибка (архив отклонён, сбой установки);
/// 3 — лаунчер уже запущен (см. App.OnStartup); 4 — отказ из-за понижения версии
/// (повторите с --allow-downgrade, если нужна именно эта сборка). Причина отказа
/// пишется в поток ошибок и в журнал лаунчера.
///
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
    internal const int ExitInstalled = 0;
    internal const int ExitFailed = 1;
    internal const int ExitDowngradeRefused = 4;

    public static async Task<int> RunAsync(
        MainWindow window, string archivePath, bool silent, bool allowDowngrade)
    {
        try
        {
            var result = await window.InstallFromLocalArchiveCliAsync(archivePath, silent, allowDowngrade);
            if (result.Status != LocalArchiveInstallStatus.Installed && !string.IsNullOrEmpty(result.Message))
                Console.Error.WriteLine($"Ошибка: {result.Message}");
            return ToExitCode(result.Status);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ошибка: {ex.Message}");
            return ExitFailed;
        }
    }

    /// <summary>Код возврата процесса по исходу установки.</summary>
    internal static int ToExitCode(LocalArchiveInstallStatus status) => status switch
    {
        LocalArchiveInstallStatus.Installed => ExitInstalled,
        LocalArchiveInstallStatus.DowngradeRefused => ExitDowngradeRefused,
        _ => ExitFailed,
    };
}
