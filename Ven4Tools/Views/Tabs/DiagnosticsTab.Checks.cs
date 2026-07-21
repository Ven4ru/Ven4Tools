using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class DiagnosticsTab : UserControl
    {
        private async Task RunDiskCheckAsync()
        {
            pnlDisks.Children.Clear();
            try
            {
                var disks = await SystemHealthService.GetDiskHealthAsync();
                if (disks.Count == 0)
                {
                    pnlDisks.Children.Add(new TextBlock { Text = "Диски не найдены.", Foreground = (Brush)FindResource("TextSecondary") });
                    return;
                }
                foreach (var disk in disks)
                {
                    if (disk.Health != DiskHealth.Healthy) _lastRunHadCritical = true;
                    string icon = disk.Health switch
                    {
                        DiskHealth.Healthy => "🟢",
                        DiskHealth.Warning => "🟡",
                        DiskHealth.Unhealthy => "🔴",
                        _ => "⚪"
                    };
                    string label = disk.Health switch
                    {
                        DiskHealth.Healthy => "исправен",
                        DiskHealth.Warning => "предупреждение",
                        DiskHealth.Unhealthy => "неисправен",
                        _ => "неизвестно"
                    };
                    pnlDisks.Children.Add(new TextBlock
                    {
                        Text = $"{icon} {disk.Name} — {label}",
                        Margin = new Thickness(0, 2, 0, 2),
                        Foreground = (Brush)FindResource("TextPrimary")
                    });
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsTab.RunDiskCheckAsync");
                pnlDisks.Children.Add(new TextBlock { Text = "Недоступно: не удалось получить состояние дисков.", Foreground = (Brush)FindResource("StatusWarning") });
            }
        }

        private async Task RunWindowsUpdateCheckAsync()
        {
            pnlWindowsUpdateFailures.Children.Clear();
            btnClearWuCache.Visibility = Visibility.Collapsed;
            try
            {
                var failures = await SystemHealthService.GetWindowsUpdateFailuresAsync();
                if (failures.Count == 0)
                {
                    pnlWindowsUpdateFailures.Children.Add(new TextBlock
                    {
                        Text = "За последние 7 дней ошибок обновления Windows не найдено.",
                        Foreground = (Brush)FindResource("StatusSuccess")
                    });
                    return;
                }

                _lastRunHadWarning = true;
                foreach (var f in failures.Take(20))
                {
                    pnlWindowsUpdateFailures.Children.Add(new TextBlock
                    {
                        Text = $"🟡 {f.TimeCreated:g} — {f.Message}",
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 2),
                        Foreground = (Brush)FindResource("TextPrimary")
                    });
                }
                btnClearWuCache.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsTab.RunWindowsUpdateCheckAsync");
                pnlWindowsUpdateFailures.Children.Add(new TextBlock { Text = "Недоступно: не удалось прочитать журнал Windows Update.", Foreground = (Brush)FindResource("StatusWarning") });
            }
        }

        private async void BtnClearWuCache_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Остановить службы обновления Windows и очистить кэш загрузки? Службы будут перезапущены автоматически.",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            btnClearWuCache.IsEnabled = false;
            try
            {
                await SystemHealthService.ClearWindowsUpdateCacheAsync();
                AppLogger.Write("🧹 Кэш Windows Update очищен");
                MessageBox.Show("✅ Кэш Windows Update очищен, службы перезапущены.", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка очистки кэша Windows Update: {ex.Message}");
                MessageBox.Show("Не удалось очистить кэш. Запустите приложение от имени администратора и попробуйте ещё раз.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnClearWuCache.IsEnabled = true;
            }
        }

        private async Task RunHardwareEventsCheckAsync()
        {
            try
            {
                var summary = await SystemHealthService.GetHardwareEventsAsync();
                txtHardwareSummary.Text =
                    $"Аппаратных ошибок (WHEA): {summary.WheaCount}. Сбоев видеодрайвера: {summary.DisplayDriverCrashCount}.";

                if (summary.RawEntries.Count > 0)
                {
                    txtHardwareRaw.Text = string.Join(Environment.NewLine, summary.RawEntries);
                    txtHardwareRaw.Visibility = Visibility.Visible;
                }
                else
                {
                    txtHardwareRaw.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsTab.RunHardwareEventsCheckAsync");
                txtHardwareSummary.Text = "Недоступно: не удалось прочитать аппаратные события.";
            }
        }
    }
}
