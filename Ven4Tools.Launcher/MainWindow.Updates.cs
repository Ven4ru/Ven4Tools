using System;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    public partial class MainWindow
    {
        private async Task CheckForUpdatesAsync()
        {
            try
            {
                AddLog("🔍 Проверка обновлений лаунчера...");
                btnCheckUpdates.IsEnabled = false;

                // Кнопка обновляет не только статус лаунчера, но и список
                // клиентских версий (CDN + GitHub-релизы).
                await LoadVersionsAsync();

                var updateSvc = new LauncherUpdateService(AddLog);
                var updateInfo = await updateSvc.CheckForUpdateAsync();

                if (updateInfo != null && updateInfo.HasUpdate)
                {
                    AddLog($"📢 Найдено обновление лаунчера: {updateInfo.LatestVersion}");
                    AddLog($"📝 {updateInfo.ReleaseNotes}");
                    btnInstallUpdate.Visibility = Visibility.Visible;
                }
                else
                {
                    AddLog("✅ У вас последняя версия лаунчера");
                    btnInstallUpdate.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка проверки обновлений: {ex.Message}");
            }
            finally
            {
                btnCheckUpdates.IsEnabled = true;
            }
        }

        private void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (_isUiTestMode)
            {
                AddLog("UI test: проверка обновлений");
                return;
            }

            _ = CheckForUpdatesAsync();
        }

        private async void BtnInstallUpdate_Click(object sender, RoutedEventArgs e)
        {
            await InstallUpdateCoreAsync();
        }

        private async Task InstallUpdateCoreAsync()
        {
            try
            {
                AddLog("📥 Начинаем скачивание обновления лаунчера...");
                btnInstallUpdate.Visibility = Visibility.Collapsed;

                var updateSvc = new LauncherUpdateService(AddLog);

                // Сначала узнаём, какую версию ставим, чтобы взять с CDN хеш именно для неё.
                var updateInfo = await updateSvc.CheckForUpdateAsync();

                // SHA256 установщика берём из version.json CDN — но только если версия
                // на CDN совпадает с устанавливаемой (иначе хеш относится к другому билду).
                // Без подтверждённого хеша обновление не выполняется (fail-closed).
                // Тот же хелпер, что использует OfferInstallationAsync — единая проверка
                // строгости хеша (LauncherUpdateService.IsValidSha256), без дублирования.
                string? expectedSha256 = null;
                string? fallbackUrl = null;
                if (updateInfo?.HasUpdate == true)
                {
                    (expectedSha256, fallbackUrl) =
                        await LauncherUpdateService.GetSetupHashFromCdnAsync(updateInfo.LatestVersion!);
                }

                var result = await updateSvc.DownloadAndRunSetupUpdateAsync(updateInfo, expectedSha256, fallbackUrl);

                if (result)
                {
                    // Установщик запущен в режиме самообновления: он дождётся завершения
                    // этого процесса, заменит exe и перезапустит лаунчер.
                    AddLog("✅ Установщик обновления запущен. Лаунчер перезапустится через несколько секунд...");
                    await Task.Delay(500);
                    ExitApplication();
                }
                else
                {
                    AddLog("❌ Обновление не выполнено — подробности выше в журнале");
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка: {ex.Message}");
            }
            finally
            {
                if (IsLoaded)
                    btnInstallUpdate.Visibility = Visibility.Visible;
            }
        }
    }
}
