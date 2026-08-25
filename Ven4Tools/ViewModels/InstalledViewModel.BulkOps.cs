using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class InstalledViewModel
    {
        // ── Обновить всё (winget upgrade --all) ─────────────────────────────────

        private async Task RunUpgradeAllAsync()
        {
            if (IsUpgradingAll) return;

            // Общий семафор с каталогом/историей/Windows Update — иначе winget
            // upgrade --all может пойти параллельно с установкой из другой вкладки
            // (конфликт msiexec, ошибка 1618).
            if (Views.UiGuards.WarnIfInstallBusy()) return;

            var res = MessageBox.Show(
                "Обновить все приложения через winget?\n\nЭто может занять продолжительное время.",
                "Обновить всё", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            // Массовое обновление — как и остальные массовые операции вкладки
            // (обновление выбранных/групповое удаление/импорт) предлагаем точку восстановления.
            var rpOutcome = await Views.UiGuards.ConfirmAndCreateRestorePointAsync(
                "Будут обновлены все приложения через winget.\n\nСоздать точку восстановления Windows перед обновлением?",
                "Ven4Tools — перед обновлением всех приложений");
            if (rpOutcome == Views.RestorePointOutcome.Cancelled) return;

            IsUpgradingAll = true;
            AppLogger.Write("⬆ Запуск обновления всех приложений (winget upgrade --all)...");
            await InstallationService.InstallSemaphore.WaitAsync();
            try
            {
                int code = await WingetRunner.RunStreamingAsync(
                    $"upgrade --all --silent --include-unknown {WingetArgs.ModifyLine}",
                    msg => AppLogger.Write(msg));
                var upgrade = DescribeWingetExitCode(code);
                if (upgrade.Success)
                    AppLogger.Write(upgrade.Reboot
                        ? "✅ Обновление завершено. Для применения некоторых обновлений требуется перезагрузка."
                        : "✅ Обновление всех приложений завершено");
                // code == -1 — синтетический признак «winget вообще не отработал»
                else if (code != -1)
                    AppLogger.Write($"⚠ {upgrade.Reason}");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка обновления: {ex.Message}");
            }
            finally
            {
                InstallationService.InstallSemaphore.Release();
                IsUpgradingAll = false;
                // Обновляем список установленных приложений после завершения
                await LoadAppsAsync();
            }
        }

        private async Task RunUpdateSelectedAsync()
        {
            if (IsUpdatingSelected) return;
            try
            {
                if (Views.UiGuards.WarnIfInstallBusy()) return;

                var visible = DisplayedApps.Where(a => a.IsSelected && a.HasUpdate).ToList();
                if (visible.Count == 0) return;

                if (visible.Count >= 2)
                {
                    var rpOutcome = await Views.UiGuards.ConfirmAndCreateRestorePointAsync(
                        $"Будет обновлено {visible.Count} приложений.\n\nСоздать точку восстановления Windows перед обновлением?",
                        "Ven4Tools — перед массовым обновлением");
                    if (rpOutcome == Views.RestorePointOutcome.Cancelled) return;
                }

                IsUpdatingSelected = true;
                foreach (var app in visible)
                    await UpdateAppAsync(app);
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка: {ex.Message}"); }
            finally { IsUpdatingSelected = false; }
        }

        private async Task RunUpdateAppAsync(InstalledApp? app)
        {
            try
            {
                if (app == null) return;
                if (Views.UiGuards.WarnIfInstallBusy()) return;
                await UpdateAppAsync(app);
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка: {ex.Message}"); }
        }

        private async Task RunUninstallAppAsync(InstalledApp? app)
        {
            try
            {
                if (app == null) return;
                if (Views.UiGuards.WarnIfInstallBusy()) return;

                var res = MessageBox.Show(
                    $"Удалить «{app.Name}»?",
                    "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res != MessageBoxResult.Yes) return;

                await UninstallAppAsync(app);
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка: {ex.Message}"); }
        }

        // ── Операции winget ────────────────────────────────────────────────────

        private async Task UpdateAppAsync(InstalledApp app)
        {
            app.IsProcessing = true;
            AppLogger.Write($"⬆ Обновление {app.Name}...");
            // Общий семафор с каталогом/историей/Windows Update — исключает параллельный
            // msiexec (ошибка 1618) при обновлении одновременно с установкой из другой вкладки.
            await InstallationService.InstallSemaphore.WaitAsync();
            try
            {
                // Усечённый в списке ID (winget list рисует "…" при узкой колонке) не пройдёт
                // валидацию WingetRunner.ValidateArgs — не пытаемся, чтобы не ловить неясную ошибку.
                if (string.IsNullOrWhiteSpace(app.WingetId) || app.WingetId.Contains('…'))
                {
                    AppLogger.Write($"⚠ {app.Name}: ID приложения усечён winget — обновление недоступно");
                    return;
                }

                // RunStreamingAsync: живой прогресс в лог + 15-минутный таймаут
                string args = $"upgrade --id \"{app.WingetId}\" --silent {WingetArgs.ModifyLine}";
                int code = await WingetRunner.RunStreamingAsync(args, line => AppLogger.Write($"  {line}"),
                    TimeSpan.FromMinutes(15));
                var exit = DescribeWingetExitCode(code);
                if (exit.Success)
                {
                    // Успех, в т.ч. коды «требуется перезагрузка» (3010 / 0x8A15002C)
                    app.Available = "";
                    Application.Current.Dispatcher.Invoke(() => { ApplyFilter(); RecomputeStats(); });
                    AppLogger.Write(exit.Reboot
                        ? $"✅ {app.Name} обновлён (требуется перезагрузка для завершения)"
                        : $"✅ {app.Name} обновлён");
                }
                // code == -1 (таймаут/принудительно завершён) не логируем здесь — обрабатывается отдельно
                else if (code != -1)
                {
                    AppLogger.Write($"⚠ {app.Name}: {exit.Reason}");
                }
            }
            catch (Exception ex) { AppLogger.Write($"❌ {app.Name}: {ex.Message}"); }
            finally
            {
                InstallationService.InstallSemaphore.Release();
                app.IsProcessing = false;
            }
        }

        private async Task UninstallAppAsync(InstalledApp app)
        {
            app.IsProcessing = true;
            AppLogger.Write($"🗑 Удаление {app.Name}...");
            // Общий семафор — см. комментарий в UpdateAppAsync.
            await InstallationService.InstallSemaphore.WaitAsync();
            try
            {
                bool ok = await AppUninstallService.TryUninstallAsync(app.WingetId, app.Name);
                if (ok)
                {
                    _allApps.Remove(app);
                    ApplyFilter();
                    AppLogger.Write($"✅ {app.Name} удалён");
                }
                else
                {
                    AppLogger.Write($"⚠ {app.Name}: деинсталлятор не найден");
                }
            }
            catch (Exception ex) { AppLogger.Write($"❌ {app.Name}: {ex.Message}"); }
            finally
            {
                InstallationService.InstallSemaphore.Release();
                app.IsProcessing = false;
            }
        }

        // ── Групповое удаление ────────────────────────────────────────────────

        private async Task RunUninstallSelectedAsync()
        {
            if (IsUninstallingSelected) return;
            try
            {
                if (Views.UiGuards.WarnIfInstallBusy()) return;

                var selected = DisplayedApps.Where(a => a.IsSelected && a.CanAct).ToList();
                if (selected.Count == 0) return;

                string list = string.Join("\n", selected.Take(10).Select(a => $"  • {a.Name}"));
                if (selected.Count > 10) list += $"\n  ... и ещё {selected.Count - 10}";

                var res = MessageBox.Show(
                    $"Удалить {selected.Count} приложений?\n\n{list}",
                    "Подтверждение удаления", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;

                if (selected.Count >= 2)
                {
                    var rpOutcome = await Views.UiGuards.ConfirmAndCreateRestorePointAsync(
                        $"Будет удалено {selected.Count} приложений.\n\nСоздать точку восстановления Windows перед удалением?",
                        "Ven4Tools — перед групповым удалением");
                    if (rpOutcome == Views.RestorePointOutcome.Cancelled) return;
                }

                IsUninstallingSelected = true;

                foreach (var app in selected)
                    await UninstallAppAsync(app);
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка: {ex.Message}"); }
            finally { IsUninstallingSelected = false; }
        }
    }
}
