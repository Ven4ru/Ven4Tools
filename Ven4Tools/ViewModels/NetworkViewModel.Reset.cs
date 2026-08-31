using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class NetworkViewModel
    {
        // ── Сброс сети ───────────────────────────────────────────────────────

        private async Task RunResetNetworkAsync()
        {
            // Гейт реентерабельности — см. пояснение в RunAllAsync. Здесь он особенно
            // важен: без него повторный клик до отключения кнопки покажет второй диалог
            // подтверждения и может запустить вторую цепочку netsh параллельно.
            if (IsResettingNetwork) return;
            var confirm = MessageBox.Show(
                "Сброс сетевых настроек:\n\n" +
                "• netsh winsock reset\n• netsh int ip reset\n• ipconfig /release\n• ipconfig /renew\n\n" +
                "Потребуются права администратора и перезагрузка.\n\nПродолжить?",
                "Сброс сети", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            IsResettingNetwork = true;
            try
            {
                AppLogger.Write("[Сеть] Запуск сброса сетевых настроек...");
                // Приложение уже работает с правами администратора (перезапуск через UAC
                // в MainWindow), поэтому runas не нужен — запускаем скрыто и перенаправляем
                // вывод команд в лог-панель вместо отдельного окна консоли.
                var psi = new ProcessStartInfo
                {
                    FileName  = TrustedExecutablePaths.CmdExe,
                    Arguments = "/c netsh winsock reset & netsh int ip reset & " +
                                "ipconfig /release & ipconfig /renew",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    WindowStyle            = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true
                };
                int exitCode = -1;
                using var p = Process.Start(psi);
                if (p != null)
                {
                    var stdoutTask = p.StandardOutput.ReadToEndAsync();
                    var stderrTask = p.StandardError.ReadToEndAsync();
                    await p.WaitForExitAsync();
                    exitCode = p.ExitCode;

                    foreach (var line in (await stdoutTask).Split('\n'))
                    {
                        var t = line.Trim();
                        if (!string.IsNullOrWhiteSpace(t)) AppLogger.Write($"[Сеть] {t}");
                    }
                    var err = (await stderrTask).Trim();
                    if (!string.IsNullOrWhiteSpace(err)) AppLogger.Write($"[Сеть] ⚠ {err}");
                }

                // Цепочка команд через «&» возвращает код последней, ненулевой код
                // означает, что часть сброса не удалась (нет прав, DHCP и т.п.).
                if (exitCode == 0)
                {
                    AppLogger.Write("[Сеть] Сброс завершён");
                    MessageBox.Show("Перезагрузите компьютер для применения изменений.",
                        "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    AppLogger.Write($"[Сеть] ⚠ Сброс завершился с кодом {exitCode} — часть команд могла не выполниться");
                    MessageBox.Show(
                        $"Сброс сетевых настроек завершился с ошибкой (код {exitCode}). Часть команд могла не выполниться.\n\n" +
                        "Запустите приложение от имени администратора и попробуйте ещё раз. Подробности — в логах.",
                        "Сброс не завершён", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Сеть] Ошибка сброса: {ex.Message}");
                MessageBox.Show("Не удалось сбросить сетевые настройки. Запустите приложение от имени администратора и попробуйте ещё раз.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            // Оригинал сбрасывает IsEnabled безусловно (без "if (!_busy)") — сброс сети
            // не вызывается из RunAllAsync, так что busy-гонки здесь нет по построению.
            finally { IsResettingNetwork = false; }
        }
    }
}
