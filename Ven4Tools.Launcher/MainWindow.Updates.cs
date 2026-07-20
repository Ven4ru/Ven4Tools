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

                var updateSvc = new LauncherUpdateService(AddLog, _downloadSource);
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
            if (_isUiTestMode)
            {
                AddLog("UI test: установка обновления лаунчера");
                return;
            }

            await InstallUpdateCoreAsync();
        }

        private async Task InstallUpdateCoreAsync()
        {
            try
            {
                AddLog("📥 Начинаем скачивание обновления лаунчера...");
                btnInstallUpdate.Visibility = Visibility.Collapsed;

                var updateSvc = new LauncherUpdateService(AddLog, _downloadSource);

                // CheckForUpdateAsync уже возвращает готовый UpdateInfo с SHA256 и
                // ссылками-кандидатами (CDN version.json — основной источник, GitHub —
                // резерв). Загрузка идёт по всей цепочке источников с учётом выбранного
                // пользователем предпочтения; без подтверждённого хеша обновление не
                // выполняется (fail-closed — проверяется внутри DownloadAndRunSetupUpdateAsync).
                var updateInfo = await updateSvc.CheckForUpdateAsync();

                var result = await updateSvc.DownloadAndRunSetupUpdateAsync(updateInfo);

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
