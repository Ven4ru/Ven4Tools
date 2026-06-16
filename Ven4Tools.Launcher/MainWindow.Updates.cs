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

                // SHA256 exe лаунчера берём из version.json CDN — но только если версия
                // на CDN совпадает с устанавливаемой (иначе хеш относится к другому билду).
                string? expectedSha256 = null;
                if (updateInfo?.HasUpdate == true)
                {
                    try
                    {
                        using var cdnService = new CdnService();
                        CdnVersionInfo? cdnInfo = await cdnService.GetVersionInfoAsync();
                        if (cdnInfo?.Launcher != null &&
                            !string.IsNullOrWhiteSpace(cdnInfo.Launcher.ExeSha256) &&
                            string.Equals(cdnInfo.Launcher.Version, updateInfo.LatestVersion, StringComparison.OrdinalIgnoreCase))
                        {
                            expectedSha256 = cdnInfo.Launcher.ExeSha256;
                        }
                    }
                    catch { /* CDN недоступен — обновляемся без хеш-проверки (как раньше) */ }
                }

                var result = await updateSvc.DownloadAndApplyUpdateAsync(updateInfo, expectedSha256);

                if (result)
                {
                    // Скрипт запущен: через 2 сек скопирует новый exe и перезапустит лаунчер.
                    // Выходим, чтобы освободить exe для замены.
                    AddLog("✅ Скрипт обновления запущен. Лаунчер перезапустится через несколько секунд...");
                    await Task.Delay(500);
                    ExitApplication();
                }
                else
                {
                    AddLog("❌ Ошибка при установке обновления");
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
