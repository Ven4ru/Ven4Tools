using System;
using System.IO;
using System.Text;
using System.Windows;
using Ven4Tools.Services;
using Ven4Tools.Services.DiskBenchmark;

namespace Ven4Tools.ViewModels
{
    public sealed partial class BenchmarkViewModel
    {
        private void CopyReport()
        {
            if (_lastResult == null) return;

            try
            {
                Clipboard.SetText(BenchmarkReportBuilder.Build(_lastResult));
                AppLogger.Write("📤 Отчёт теста скорости диска скопирован в буфер обмена");
                MessageBox.Show("Отчёт скопирован в буфер обмена.", "Готово",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "BenchmarkViewModel.CopyReport");
                MessageBox.Show("Не удалось скопировать отчёт: " + ex.Message, "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveReport()
        {
            if (_lastResult == null) return;

            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Сохранить отчёт теста скорости диска",
                    Filter = "Текстовый файл (*.txt)|*.txt",
                    DefaultExt = ".txt",
                    FileName = $"Ven4Tools_тест_диска_{DateTime.Now:yyyy-MM-dd_HH-mm}.txt"
                };

                if (dialog.ShowDialog() != true) return;

                File.WriteAllText(dialog.FileName, BenchmarkReportBuilder.Build(_lastResult), Encoding.UTF8);
                AppLogger.Write("💾 Отчёт теста скорости диска сохранён в файл");
                MessageBox.Show("Отчёт сохранён.", "Готово",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "BenchmarkViewModel.SaveReport");
                MessageBox.Show("Не удалось сохранить отчёт: " + ex.Message, "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
