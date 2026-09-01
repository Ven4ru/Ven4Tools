using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.Services.DiskBenchmark;

namespace Ven4Tools.ViewModels
{
    public sealed partial class BenchmarkViewModel
    {
        private async Task RunBenchmarkAsync()
        {
            // Во время прогона та же кнопка останавливает тест.
            if (_running)
            {
                _cancellation?.Cancel();
                IsRunEnabled = false;
                RunStatusText = "Останавливаем...";
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

            RunButtonText = "⏹ Остановить";
            IsCopyReportEnabled = false;
            IsSaveReportEnabled = false;
            IsControlsEnabled = false;
            ShowProgress = true;
            ProgressValue = 0;
            ClearResults();

            var progress = new Progress<BenchmarkProgress>(report =>
            {
                RunStatusText = report.Stage;
                ProgressValue = Math.Max(0, Math.Min(100, report.Fraction * 100));
            });

            AppLogger.Write($"⏱️ Запущен тест скорости диска: {_selectedDisk.FriendlyName}, " +
                            $"том {_selectedVolume.Letter}, профиль {BenchmarkPresets.DescribeProfile(SelectedProfile)}");

            try
            {
                var result = await DiskBenchmarkEngine.RunAsync(
                    _selectedDisk, _selectedVolume, SelectedProfile, SelectedFileSize,
                    progress, _cancellation.Token);

                foreach (string warning in _warnings) result.Warnings.Add(warning);

                _lastResult = result;
                ShowResults(result);

                RunStatusText = result.Cancelled
                    ? "Тест остановлен, показаны частичные результаты"
                    : "Готово за " + BenchmarkReportBuilder.FormatDuration(result.Duration);

                IsCopyReportEnabled = result.Measurements.Count > 0;
                IsSaveReportEnabled = result.Measurements.Count > 0;

                AppLogger.Write(result.Cancelled
                    ? "⏹️ Тест скорости диска остановлен пользователем"
                    : "✅ Тест скорости диска завершён");
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "BenchmarkViewModel.RunBenchmarkAsync");
                RunStatusText = "Не удалось выполнить тест";
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

                RunButtonText = "▶ Запустить тест";
                IsRunEnabled = true;
                IsControlsEnabled = true;
                ShowProgress = false;
            }
        }

        private List<BenchmarkResultRow> BuildEmptyResultRows()
        {
            var rows = new List<BenchmarkResultRow>();
            for (int index = 0; index < BenchmarkPresets.Patterns.Length; index++)
            {
                rows.Add(new BenchmarkResultRow
                {
                    Index = index,
                    Name = BenchmarkPresets.Patterns[index].Name,
                    ReadValueText = "—",
                    ReadSubText = "",
                    WriteValueText = "—",
                    WriteSubText = ""
                });
            }
            return rows;
        }

        internal void ClearResults()
        {
            ResultRows = BuildEmptyResultRows();
            ConclusionLines = new[]
            {
                new ConclusionLine { Text = "Идёт измерение...", ForegroundKey = "TextSecondary" }
            };
        }

        internal void ShowResults(BenchmarkRunResult result)
        {
            var rows = new List<BenchmarkResultRow>();
            for (int index = 0; index < BenchmarkPresets.Patterns.Length; index++)
            {
                string name = BenchmarkPresets.Patterns[index].Name;
                var read = result.Find(name, BenchmarkOperation.Read);
                var write = result.Find(name, BenchmarkOperation.Write);
                rows.Add(new BenchmarkResultRow
                {
                    Index = index,
                    Name = name,
                    ReadValueText = FormatCellValue(read),
                    ReadSubText = FormatCellSub(read),
                    WriteValueText = FormatCellValue(write),
                    WriteSubText = FormatCellSub(write)
                });
            }
            ResultRows = rows;

            ShowConclusions(result);
        }

        private static string FormatCellValue(BenchmarkMeasurement? measurement) =>
            measurement == null ? "—" : BenchmarkReportBuilder.FormatSpeed(measurement.MegabytesPerSecond) + " МБ/с";

        private static string FormatCellSub(BenchmarkMeasurement? measurement) =>
            measurement == null ? "" : BenchmarkReportBuilder.FormatIops(measurement.OperationsPerSecond) +
                                        " оп/с · задержка " +
                                        BenchmarkReportBuilder.FormatLatency(measurement.AverageLatencyMicroseconds);

        private void ShowConclusions(BenchmarkRunResult result)
        {
            var lines = new List<ConclusionLine>();

            if (result.Measurements.Count == 0)
            {
                lines.Add(MakeConclusion("Замеры не выполнены."));
                ConclusionLines = lines;
                return;
            }

            if (result.Cancelled)
                lines.Add(MakeConclusion("Тест остановлен досрочно — показаны только успевшие завершиться замеры."));

            var sequentialRead = result.Find("SEQ1M Q8T1", BenchmarkOperation.Read);
            if (sequentialRead != null)
            {
                lines.Add(MakeConclusion("Последовательное чтение " +
                              BenchmarkReportBuilder.FormatSpeedRounded(sequentialRead.MegabytesPerSecond) +
                              " МБ/с — " +
                              BenchmarkReportBuilder.DescribeLevel(sequentialRead.MegabytesPerSecond) + "."));

                var disk = result.Disk;
                if (disk != null && disk.Link.IsKnown && disk.Link.CeilingMegabytesPerSecond > 0)
                {
                    double share = sequentialRead.MegabytesPerSecond / disk.Link.CeilingMegabytesPerSecond * 100;
                    lines.Add(MakeConclusion("Накопитель выбирает " + BenchmarkReportBuilder.FormatPercent(share) +
                                  "% пропускной способности интерфейса (" +
                                  BenchmarkReportBuilder.FormatSpeedRounded(disk.Link.CeilingMegabytesPerSecond) +
                                  " МБ/с)."));
                }
                else
                {
                    lines.Add(MakeConclusion("Потолок интерфейса не определён, поэтому долю его использования " +
                                  "показать нельзя — приблизительное значение здесь только вводило бы в заблуждение."));
                }
            }

            var randomRead = result.Find("RND4K Q1T1", BenchmarkOperation.Read);
            if (randomRead != null && randomRead.AverageLatencyMicroseconds > 0)
            {
                lines.Add(MakeConclusion("Задержка одиночного случайного чтения — " +
                              BenchmarkReportBuilder.FormatLatency(randomRead.AverageLatencyMicroseconds) +
                              ". Отзывчивость системы и скорость запуска программ зависят от неё " +
                              "сильнее, чем от последовательной скорости."));
            }

            ConclusionLines = lines;
        }

        private static ConclusionLine MakeConclusion(string text) =>
            new() { Text = "• " + text, ForegroundKey = "TextPrimary" };
    }
}
