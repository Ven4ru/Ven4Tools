using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    public partial class MainWindow
    {
        private void BtnInstallFromFile_Click(object sender, RoutedEventArgs e)
        {
            if (_isUiTestMode)
            {
                AddLog("UI test: установка из локального файла");
                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Архив клиента Ven4Tools (*.zip)|*.zip",
                Title = "Выберите архив клиента"
            };
            if (dialog.ShowDialog() != true) return;

            _downloadCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            _ = InstallFromLocalArchiveAsync(dialog.FileName, _downloadCts.Token, silent: false);
        }

        internal async Task InstallFromLocalArchiveAsync(string archivePath, CancellationToken token, bool silent)
        {
            Dispatcher.Invoke(() =>
            {
                progressDownload.Value = 0;
                txtDownloadStatus.Text = "Проверка подписи...";
                btnCancelDownload.Visibility = silent ? Visibility.Collapsed : Visibility.Visible;
                btnLaunchApp.IsEnabled = false;
            });
            Dispatcher.Invoke(() => SetOperationStage(2)); // Проверка целостности

            try
            {
                AddLog($"📂 Установка из локального файла: {archivePath}");
                using var cdnService = new CdnService();
                var result = await LocalArchiveVerifier.VerifyAsync(archivePath, cdnService, token);

                if (result.Outcome == LocalArchiveOutcome.Rejected)
                {
                    Dispatcher.Invoke(() => txtDownloadStatus.Text = "Отклонено");
                    Dispatcher.Invoke(() => SetOperationStage(0));
                    AddLog($"⛔ {result.RejectionReason}");
                    if (!silent)
                        Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                            result.RejectionReason, "Установка отклонена",
                            MessageBoxButton.OK, MessageBoxImage.Error));
                    return;
                }

                AddLog(result.Outcome == LocalArchiveOutcome.Offline
                    ? $"✅ Офлайн-подпись подтверждена (версия {result.Version})"
                    : $"✅ Подтверждено по списку исторических версий (версия {result.Version})");

                if (result.Outcome == LocalArchiveOutcome.Historical)
                {
                    string warning =
                        $"Это архивная версия {result.Version} — подтверждена по списку ранее опубликованных " +
                        "версий, но не имеет встроенной подписи.\n\nРекомендуем скачать актуальную версию через " +
                        "обычную загрузку.\n\nВсё равно установить архивную версию?";
                    AddLog($"⚠️ Архивная версия {result.Version} без встроенной подписи, подтверждена по сети");

                    if (!silent)
                    {
                        var answer = Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                            warning, "Архивная версия", MessageBoxButton.YesNo, MessageBoxImage.Warning));
                        if (answer != MessageBoxResult.Yes)
                        {
                            Dispatcher.Invoke(() => txtDownloadStatus.Text = "Отменено");
                            Dispatcher.Invoke(() => SetOperationStage(0));
                            AddLog("⏹ Установка архивной версии отменена пользователем");
                            return;
                        }
                    }
                }

                bool installed = await ExtractAndInstallClientAsync(archivePath, result.Version ?? "?", token, silent);
                if (installed && !silent)
                    Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                        $"Клиент {result.Version} успешно установлен в:\n{_clientPath}",
                        "Установка завершена", MessageBoxButton.OK, MessageBoxImage.Information));
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() => { txtDownloadStatus.Text = "Отменено"; progressDownload.Value = 0; });
                Dispatcher.Invoke(() => SetOperationStage(0));
                AddLog("⏹ Установка из файла отменена");
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "Ошибка");
                Dispatcher.Invoke(() => SetOperationStage(0));
                AddLog($"❌ Ошибка установки из файла: {ex.Message}");
                if (!silent)
                    Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                        $"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error));
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    btnCancelDownload.Visibility = Visibility.Collapsed;
                    btnCancelDownload.IsEnabled = true;
                    btnLaunchApp.IsEnabled = true;
                });
                _downloadCts?.Dispose();
                _downloadCts = null;
            }
        }
    }
}
