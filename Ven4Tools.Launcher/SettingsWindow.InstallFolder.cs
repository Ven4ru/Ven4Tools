using System;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    // Блоки окна настроек, относящиеся к папке клиента и фоновым загрузкам:
    // «Не скачивать обновления в фоне по лимитному подключению» (в «Поведении») и
    // «Папка клиента» с кнопкой «Перенести клиент…».
    public partial class SettingsWindow
    {
        private bool _suppressMeteredChange;

        private void InitInstallFolderBlock()
        {
            _suppressMeteredChange = true;
            chkDeferOnMetered.IsChecked = _owner.DeferUpdatesOnMetered;
            _suppressMeteredChange = false;

            _ = RefreshClientFolderInfoAsync();
        }

        private void ChkDeferOnMetered_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressMeteredChange) return;
            _owner.OnDeferUpdatesOnMeteredChanged(chkDeferOnMetered.IsChecked == true);
        }

        // Текущая папка и её риски. Состояние CFA приходит из WMI до секунды, поэтому
        // путь показывается сразу, а предупреждение — когда будет ответ.
        private async Task RefreshClientFolderInfoAsync()
        {
            string path = _owner.CurrentClientPath;
            txtClientFolderPath.Text = path;
            txtClientFolderRisk.Visibility = Visibility.Collapsed;

            try
            {
                var cfa = await InstallLocationRisk.GetCfaStatusAsync();
                var risk = await Task.Run(() => InstallLocationRisk.Assess(path, cfa));
                if (risk.IsRisky)
                {
                    txtClientFolderRisk.Text = risk.Describe();
                    txtClientFolderRisk.Visibility = Visibility.Visible;
                }
            }
            catch
            {
                // Нет данных — не пугаем.
            }
        }

        private async void BtnMoveClient_Click(object sender, RoutedEventArgs e)
        {
            btnMoveClient.IsEnabled = false;
            try
            {
                await _owner.MoveClientAsync();
            }
            catch (Exception ex)
            {
                // async void: исключение уронило бы весь лаунчер.
                MessageBox.Show($"Перенос клиента не выполнен: {ex.Message}",
                    "Перенос клиента", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                btnMoveClient.IsEnabled = true;
                await RefreshClientFolderInfoAsync();
            }
        }
    }
}
