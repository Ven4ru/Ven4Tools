using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.Services.DiskBenchmark;

namespace Ven4Tools.Views.Tabs
{
    public partial class BenchmarkTab : UserControl
    {
        private async void BtnRunBenchmark_Click(object sender, RoutedEventArgs e)
        {
            // Во время прогона та же кнопка останавливает тест.
            if (_running)
            {
                _cancellation?.Cancel();
                btnRunBenchmark.IsEnabled = false;
                txtRunStatus.Text = "Останавливаем...";
                return;
            }

            if (_selectedDisk == null || _selectedVolume == null) return;

            if (!BenchmarkWarningService.TryValidateFreeSpace(
                    _selectedVolume, SelectedFileSize, out string blockingError))
            {
                MessageBox.Show(blockingError, "Тест не запущен",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _running = true;
            _lastResult = null;
            _cancellation = new CancellationTokenSource();

            btnRunBenchmark.Content = "⏹ Остановить";
            btnCopyReport.IsEnabled = false;
            btnSaveReport.IsEnabled = false;
            cmbDisks.IsEnabled = false;
            cmbVolumes.IsEnabled = false;
            cmbProfile.IsEnabled = false;
            cmbFileSize.IsEnabled = false;
            progressBenchmark.Visibility = Visibility.Visible;
            progressBenchmark.Value = 0;
            ClearResults();

            var progress = new Progress<BenchmarkProgress>(report =>
            {
                txtRunStatus.Text = report.Stage;
                progressBenchmark.Value = Math.Max(0, Math.Min(100, report.Fraction * 100));
            });

            AppLogger.Write($"⏱️ Запущен тест скорости диска: {_selectedDisk.FriendlyName}, " +
                            $"том {_selectedVolume.Letter}, профиль {DiskBenchmarkEngine.DescribeProfile(SelectedProfile)}");

            try
            {
                var result = await DiskBenchmarkEngine.RunAsync(
                    _selectedDisk, _selectedVolume, SelectedProfile, SelectedFileSize,
                    progress, _cancellation.Token);

                foreach (string warning in _warnings) result.Warnings.Add(warning);

                _lastResult = result;
                ShowResults(result);

                txtRunStatus.Text = result.Cancelled
                    ? "Тест остановлен, показаны частичные результаты"
                    : "Готово за " + BenchmarkReportBuilder.FormatDuration(result.Duration);

                btnCopyReport.IsEnabled = result.Measurements.Count > 0;
                btnSaveReport.IsEnabled = result.Measurements.Count > 0;

                AppLogger.Write(result.Cancelled
                    ? "⏹️ Тест скорости диска остановлен пользователем"
                    : "✅ Тест скорости диска завершён");
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "BenchmarkTab.BtnRunBenchmark_Click");
                txtRunStatus.Text = "Не удалось выполнить тест";
                MessageBox.Show(
                    "Не удалось выполнить тест: " + ex.Message +
                    "\n\nПодробности сохранены в журнале приложения.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _running = false;
                _cancellation?.Dispose();
                _cancellation = null;

                btnRunBenchmark.Content = "▶ Запустить тест";
                btnRunBenchmark.IsEnabled = true;
                cmbDisks.IsEnabled = true;
                cmbVolumes.IsEnabled = true;
                cmbProfile.IsEnabled = true;
                cmbFileSize.IsEnabled = true;
                progressBenchmark.Visibility = Visibility.Collapsed;
            }
        }

        private void ClearResults()
        {
            for (int index = 0; index < DiskBenchmarkEngine.Patterns.Length; index++)
            {
                SetCell(index, BenchmarkOperation.Read, null);
                SetCell(index, BenchmarkOperation.Write, null);
            }

            pnlConclusions.Children.Clear();
            pnlConclusions.Children.Add(new TextBlock
            {
                Text = "Идёт измерение...",
                Foreground = (Brush)FindResource("TextSecondary")
            });
        }

        private void ShowResults(BenchmarkRunResult result)
        {
            for (int index = 0; index < DiskBenchmarkEngine.Patterns.Length; index++)
            {
                string name = DiskBenchmarkEngine.Patterns[index].Name;
                SetCell(index, BenchmarkOperation.Read, result.Find(name, BenchmarkOperation.Read));
                SetCell(index, BenchmarkOperation.Write, result.Find(name, BenchmarkOperation.Write));
            }

            ShowConclusions(result);
        }

        /// <summary>Заполняет ячейку таблицы: крупно скорость, мелко операции и задержка.</summary>
        private void SetCell(int patternIndex, BenchmarkOperation operation, BenchmarkMeasurement? measurement)
        {
            string suffix = operation == BenchmarkOperation.Read ? "Read" : "Write";
            var value = FindName($"txtP{patternIndex}{suffix}") as TextBlock;
            var details = FindName($"txtP{patternIndex}{suffix}Sub") as TextBlock;
            if (value == null || details == null) return;

            if (measurement == null)
            {
                value.Text = "—";
                details.Text = "";
                return;
            }

            value.Text = BenchmarkReportBuilder.FormatSpeed(measurement.MegabytesPerSecond) + " МБ/с";
            details.Text = BenchmarkReportBuilder.FormatIops(measurement.OperationsPerSecond) +
                           " оп/с · задержка " +
                           BenchmarkReportBuilder.FormatLatency(measurement.AverageLatencyMicroseconds);
        }

        private void ShowConclusions(BenchmarkRunResult result)
        {
            pnlConclusions.Children.Clear();

            if (result.Measurements.Count == 0)
            {
                AddConclusion("Замеры не выполнены.");
                return;
            }

            if (result.Cancelled)
                AddConclusion("Тест остановлен досрочно — показаны только успевшие завершиться замеры.");

            var sequentialRead = result.Find("SEQ1M Q8T1", BenchmarkOperation.Read);
            if (sequentialRead != null)
            {
                AddConclusion("Последовательное чтение " +
                              BenchmarkReportBuilder.FormatSpeedRounded(sequentialRead.MegabytesPerSecond) +
                              " МБ/с — " +
                              BenchmarkReportBuilder.DescribeLevel(sequentialRead.MegabytesPerSecond) + ".");

                var disk = result.Disk;
                if (disk != null && disk.Link.IsKnown && disk.Link.CeilingMegabytesPerSecond > 0)
                {
                    double share = sequentialRead.MegabytesPerSecond / disk.Link.CeilingMegabytesPerSecond * 100;
                    AddConclusion("Накопитель выбирает " + BenchmarkReportBuilder.FormatPercent(share) +
                                  "% пропускной способности интерфейса (" +
                                  BenchmarkReportBuilder.FormatSpeedRounded(disk.Link.CeilingMegabytesPerSecond) +
                                  " МБ/с).");
                }
                else
                {
                    AddConclusion("Потолок интерфейса не определён, поэтому долю его использования " +
                                  "показать нельзя — приблизительное значение здесь только вводило бы в заблуждение.");
                }
            }

            var randomRead = result.Find("RND4K Q1T1", BenchmarkOperation.Read);
            if (randomRead != null && randomRead.AverageLatencyMicroseconds > 0)
            {
                AddConclusion("Задержка одиночного случайного чтения — " +
                              BenchmarkReportBuilder.FormatLatency(randomRead.AverageLatencyMicroseconds) +
                              ". Отзывчивость системы и скорость запуска программ зависят от неё " +
                              "сильнее, чем от последовательной скорости.");
            }
        }

        private void AddConclusion(string text)
        {
            pnlConclusions.Children.Add(new TextBlock
            {
                Text = "• " + text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
                Foreground = (Brush)FindResource("TextPrimary")
            });
        }
    }
}
