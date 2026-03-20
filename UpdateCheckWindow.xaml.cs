using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;
using Ven4Tools.Models;

namespace Ven4Tools
{
    public partial class UpdateCheckWindow : Window
    {
        private UpdateService? updateService;
        private CancellationTokenSource? cancellationTokenSource;
        private UpdateInfo? updateInfo;

        private const string REPO_OWNER = "Ven4ru";
        private const string REPO_NAME = "Ven4Tools";

        private StringBuilder debugLog = new StringBuilder();
        private readonly string debugLogPath;

        public UpdateCheckWindow()
        {
            InitializeComponent();
            this.Loaded += async (s, e) => await CheckForUpdateAsync();
            this.Closing += UpdateCheckWindow_Closing;

            debugLogPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ven4Tools", "update_debug.log");
            Directory.CreateDirectory(Path.GetDirectoryName(debugLogPath)!);
        }

        private void DebugLog(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string logLine = $"[{timestamp}] {message}";

            debugLog.AppendLine(logLine);

            Dispatcher.Invoke(() =>
            {
                txtDebugLog.AppendText(logLine + "\n");
                txtDebugLog.ScrollToEnd();
            });

            try { File.AppendAllText(debugLogPath, logLine + "\n"); } catch { }
        }

        private async Task CheckForUpdateAsync()
        {
            DebugLog("=== Начало проверки обновлений ===");

            var progress = new Progress<UpdateService.UpdateProgress>(p =>
            {
                txtStatus.Text = p.Status;
                progressBar.Value = p.Percentage;

                if (p.TotalBytes > 0)
                {
                    txtSizeInfo.Text = $"{p.BytesDownloaded / 1024 / 1024:F1} МБ / {p.TotalBytes / 1024 / 1024:F1} МБ";
                    txtSizeInfo.Visibility = Visibility.Visible;
                }
            });

            updateService = new UpdateService(REPO_OWNER, REPO_NAME, progress);
            cancellationTokenSource = new CancellationTokenSource();

            try
            {
                updateInfo = await updateService.CheckForUpdateAsync();

                progressBar.Visibility = Visibility.Collapsed;
                txtSizeInfo.Visibility = Visibility.Collapsed;

                if (!string.IsNullOrEmpty(updateInfo.Error))
                {
                    ShowError(updateInfo.Error);
                    return;
                }

                if (updateInfo.HasUpdate)
                    ShowUpdateAvailable();
                else
                    ShowNoUpdate();
            }
            catch (Exception ex)
            {
                ShowError($"Ошибка: {ex.Message}");
            }

            DebugLog("=== Проверка завершена ===");
        }

        private void ShowUpdateAvailable()
        {
            txtStatus.Text = "🎉 Доступно обновление!";
            txtStatus.Foreground = System.Windows.Media.Brushes.LightGreen;

            if (txtPriority != null)
            {
                txtPriority.Text = updateInfo!.PriorityDisplay;
                txtPriority.Visibility = Visibility.Visible;

                txtPriority.Foreground = updateInfo.Priority switch
                {
                    UpdatePriority.Critical => System.Windows.Media.Brushes.Red,
                    UpdatePriority.Recommended => System.Windows.Media.Brushes.Orange,
                    _ => System.Windows.Media.Brushes.LightGreen
                };
            }

            txtVersionInfo.Text = $"Версия {updateInfo!.CurrentVersion} → {updateInfo.LatestVersion}";

            if (!string.IsNullOrEmpty(updateInfo.ReleaseNotes))
            {
                txtChangelog.Text = updateInfo.ReleaseNotes;
                scrollChangelog.Visibility = Visibility.Visible;
            }

            btnUpdate.Visibility = Visibility.Visible;

            if (updateInfo.IsCritical)
            {
                btnUpdate.Content = "🔴 ОБНОВИТЬ СЕЙЧАС";
                btnUpdate.Background = System.Windows.Media.Brushes.DarkRed;
                btnCancel.Visibility = Visibility.Collapsed;
            }

            panelResult.Visibility = Visibility.Visible;
        }

        private void ShowNoUpdate()
        {
            txtStatus.Text = "✅ У вас актуальная версия";
            txtStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
            txtVersionInfo.Text = $"Версия {updateInfo?.CurrentVersion} — последняя доступная.";
            panelResult.Visibility = Visibility.Visible;
        }

        private void ShowError(string error)
        {
            txtStatus.Text = "❌ Ошибка проверки";
            txtStatus.Foreground = System.Windows.Media.Brushes.LightCoral;
            txtVersionInfo.Text = error;
            panelResult.Visibility = Visibility.Visible;
            LogError(error);
        }

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (updateInfo?.DownloadUrl == null) return;

            try
            {
                btnUpdate.IsEnabled = false;
                btnCancel.IsEnabled = false;

                panelResult.Visibility = Visibility.Collapsed;
                progressBar.Visibility = Visibility.Visible;
                progressBar.Value = 0;

                bool success = await updateService!.DownloadAndInstallSilentlyAsync(
                    updateInfo,
                    cancellationTokenSource?.Token ?? CancellationToken.None);

                if (success)
                {
                    if (updateInfo.IsCritical)
                        Application.Current.Shutdown();
                    else
                    {
                        MessageBox.Show(
                            "Установщик обновления запущен. Программа будет закрыта.",
                            "Обновление",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        Application.Current.Shutdown();
                    }
                }
            }
            catch (Exception ex)
            {
                ShowError($"Ошибка: {ex.Message}");
                btnUpdate.IsEnabled = true;
                btnCancel.IsEnabled = true;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (updateInfo?.IsCritical == true)
            {
                MessageBox.Show(
                    "Это критическое обновление безопасности. Без него работа программы невозможна.\n\n" +
                    "Программа будет закрыта. Скачайте обновление вручную с GitHub.",
                    "Критическое обновление",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Application.Current.Shutdown();
            }
            else
            {
                cancellationTokenSource?.Cancel();
                this.Close();
            }
        }

        private void BtnCopyDebug_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(debugLog.ToString());
                MessageBox.Show("Лог скопирован в буфер обмена", "Готово",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка копирования: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClearDebug_Click(object sender, RoutedEventArgs e)
        {
            debugLog.Clear();
            txtDebugLog.Clear();
            try { File.WriteAllText(debugLogPath, string.Empty); } catch { }
        }

        private void UpdateCheckWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (updateInfo?.IsCritical == true && !updateInfo.HasUpdate)
            {
                MessageBox.Show(
                    "Это критическое обновление безопасности. Без него работа программы невозможна.\n\n" +
                    "Программа будет закрыта.",
                    "Критическое обновление",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Application.Current.Shutdown();
            }

            cancellationTokenSource?.Cancel();
            updateService?.Dispose();
        }

        private void LogError(string error)
        {
            try
            {
                string logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Ven4Tools", "update_errors.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {error}\n");
            }
            catch { }
        }
    }
}