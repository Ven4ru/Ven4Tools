using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class InstalledTab : UserControl
    {
        // ── Обновить всё (winget upgrade --all) ─────────────────────────────────

        private async void BtnUpgradeAll_Click(object sender, RoutedEventArgs e)
        {
            // Общий семафор с каталогом/историей/Windows Update — иначе winget
            // upgrade --all может пойти параллельно с установкой из другой вкладки
            // (конфликт msiexec, ошибка 1618).
            if (UiGuards.WarnIfInstallBusy()) return;

            var res = MessageBox.Show(
                "Обновить все приложения через winget?\n\nЭто может занять продолжительное время.",
                "Обновить всё", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            // Массовое обновление — как и остальные массовые операции вкладки
            // (обновление выбранных/групповое удаление/импорт) предлагаем точку восстановления.
            var rpOutcome = await UiGuards.ConfirmAndCreateRestorePointAsync(
                "Будут обновлены все приложения через winget.\n\nСоздать точку восстановления Windows перед обновлением?",
                "Ven4Tools — перед обновлением всех приложений");
            if (rpOutcome == RestorePointOutcome.Cancelled) return;

            btnUpgradeAll.IsEnabled = false;
            btnRefresh.IsEnabled = false;
            AppLogger.Write("⬆ Запуск обновления всех приложений (winget upgrade --all)...");
            await InstallationService.InstallSemaphore.WaitAsync();
            try
            {
                int code = await WingetRunner.RunStreamingAsync(
                    "upgrade --all --silent --include-unknown --accept-package-agreements --accept-source-agreements",
                    msg => AppLogger.Write(msg));
                var upgrade = DescribeWingetExitCode(code);
                if (upgrade.Success)
                    AppLogger.Write(upgrade.Reboot
                        ? "✅ Обновление завершено. Для применения некоторых обновлений требуется перезагрузка."
                        : "✅ Обновление всех приложений завершено");
                else
                    AppLogger.Write($"⚠ {upgrade.Reason}");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка обновления: {ex.Message}");
            }
            finally
            {
                InstallationService.InstallSemaphore.Release();
                btnUpgradeAll.IsEnabled = true;
                btnRefresh.IsEnabled = true;
                // Обновляем список установленных приложений после завершения
                await LoadAppsAsync();
            }
        }

        private void ChkSelectAll_Click(object sender, RoutedEventArgs e)
        {
            bool check = chkSelectAll.IsChecked == true;
            var visible = lstApps.ItemsSource as IEnumerable<InstalledApp>;
            if (visible == null) return;
            foreach (var app in visible)
                if (app.CanAct && app.HasUpdate)
                    app.IsSelected = check;
            UpdateUpdateSelectedButton();
        }

        private void ItemCheckBox_Click(object sender, RoutedEventArgs e)
        {
            UpdateUpdateSelectedButton();
            UpdateSelectAllState();
        }

        private void UpdateSelectAllState()
        {
            var visible = (lstApps.ItemsSource as IEnumerable<InstalledApp>)?.Where(a => a.HasUpdate && a.CanAct).ToList();
            if (visible == null || visible.Count == 0)
            {
                chkSelectAll.IsChecked = false;
                return;
            }
            int selected = visible.Count(a => a.IsSelected);
            chkSelectAll.IsChecked = selected == visible.Count ? true :
                                     selected == 0 ? false : (bool?)null;
        }

        private void UpdateUpdateSelectedButton()
        {
            var visible = lstApps.ItemsSource as IEnumerable<InstalledApp>;
            var selected = visible?.Where(a => a.IsSelected).ToList();
            btnUpdateSelected.IsEnabled    = selected?.Any(a => a.HasUpdate) == true;
            btnUninstallSelected.IsEnabled = selected?.Count > 0;
        }

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (((Button)sender).Tag is not InstalledApp app) return;
                if (UiGuards.WarnIfInstallBusy()) return;
                await UpdateAppAsync(app);
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка: {ex.Message}"); }
        }

        private async void BtnUninstall_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (((Button)sender).Tag is not InstalledApp app) return;
                if (UiGuards.WarnIfInstallBusy()) return;

                var res = MessageBox.Show(
                    $"Удалить «{app.Name}»?",
                    "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res != MessageBoxResult.Yes) return;

                await UninstallAppAsync(app);
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка: {ex.Message}"); }
        }

        private async void BtnUpdateSelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (UiGuards.WarnIfInstallBusy()) return;

                var visible = (lstApps.ItemsSource as IEnumerable<InstalledApp>)
                    ?.Where(a => a.IsSelected && a.HasUpdate).ToList();
                if (visible == null || visible.Count == 0) return;

                if (visible.Count >= 2)
                {
                    var rpOutcome = await UiGuards.ConfirmAndCreateRestorePointAsync(
                        $"Будет обновлено {visible.Count} приложений.\n\nСоздать точку восстановления Windows перед обновлением?",
                        "Ven4Tools — перед массовым обновлением");
                    if (rpOutcome == RestorePointOutcome.Cancelled) return;
                }

                btnUpdateSelected.IsEnabled = false;
                foreach (var app in visible)
                    await UpdateAppAsync(app);
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка: {ex.Message}"); }
            finally { btnUpdateSelected.IsEnabled = true; }
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
                // (RunAsync с 120с убивал winget на больших пакетах).
                // --locale en-US не используется — на части систем даёт пустой вывод (см. agent_context.md).
                string args = $"upgrade --id \"{app.WingetId}\" --silent --accept-package-agreements --accept-source-agreements";
                int code = await WingetRunner.RunStreamingAsync(args, line => AppLogger.Write($"  {line}"),
                    TimeSpan.FromMinutes(15));
                var exit = DescribeWingetExitCode(code);
                if (exit.Success)
                {
                    // Успех, в т.ч. коды «требуется перезагрузка» (3010 / 0x8A15002C)
                    app.Available = "";
                    Dispatcher.Invoke(() => { ApplyFilter(); UpdateStats(); });
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

        private async void BtnUninstallSelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (UiGuards.WarnIfInstallBusy()) return;

                var selected = (lstApps.ItemsSource as IEnumerable<InstalledApp>)
                    ?.Where(a => a.IsSelected && a.CanAct).ToList();
                if (selected == null || selected.Count == 0) return;

                string list = string.Join("\n", selected.Take(10).Select(a => $"  • {a.Name}"));
                if (selected.Count > 10) list += $"\n  ... и ещё {selected.Count - 10}";

                var res = MessageBox.Show(
                    $"Удалить {selected.Count} приложений?\n\n{list}",
                    "Подтверждение удаления", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;

                if (selected.Count >= 2)
                {
                    var rpOutcome = await UiGuards.ConfirmAndCreateRestorePointAsync(
                        $"Будет удалено {selected.Count} приложений.\n\nСоздать точку восстановления Windows перед удалением?",
                        "Ven4Tools — перед групповым удалением");
                    if (rpOutcome == RestorePointOutcome.Cancelled) return;
                }

                btnUninstallSelected.IsEnabled = false;

                foreach (var app in selected)
                    await UninstallAppAsync(app);

                btnUninstallSelected.IsEnabled = selected.Any(a => a.CanAct);
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка: {ex.Message}"); btnUninstallSelected.IsEnabled = true; }
        }
    }
}
