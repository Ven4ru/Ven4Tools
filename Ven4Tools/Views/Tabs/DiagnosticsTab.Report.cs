using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class DiagnosticsTab : UserControl
    {
        private List<RebootDiagnosis> _lastRebootDiagnoses = new();

        private async void BtnRunDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            btnRunDiagnostics.IsEnabled = false;
            txtHealthBadge.Text = "Диагностика выполняется...";
            dotHealthBadge.Fill = (Brush)FindResource("TextSecondary");
            _lastRunHadCritical = false;
            _lastRunHadWarning = false;

            try
            {
                _lastRebootDiagnoses = await RunRebootHistoryCheckAsync();
                await RunDiskCheckAsync();
                await RunWindowsUpdateCheckAsync();
                await RunHardwareEventsCheckAsync();

                if (_lastRunHadCritical)
                {
                    txtHealthBadge.Text = "🔴 Критично — есть находки, требующие внимания";
                    dotHealthBadge.Fill = (Brush)FindResource("StatusDanger");
                }
                else if (_lastRunHadWarning)
                {
                    txtHealthBadge.Text = "🟡 Есть на что посмотреть";
                    dotHealthBadge.Fill = (Brush)FindResource("StatusWarning");
                }
                else
                {
                    txtHealthBadge.Text = "🟢 Всё в порядке";
                    dotHealthBadge.Fill = (Brush)FindResource("StatusSuccess");
                }
                txtLastRun.Text = $"Последний запуск: {DateTime.Now:g}";
                AppLogger.Write("🔍 Диагностика ПК выполнена");
            }
            finally
            {
                btnRunDiagnostics.IsEnabled = true;
            }
        }

        private void BtnCopyFullReport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("=== Отчёт диагностики Ven4Tools ===");
                sb.AppendLine($"Время: {DateTime.Now:g}");
                sb.AppendLine();
                sb.AppendLine($"ОС: {txtOSVersion.Text}");
                sb.AppendLine($"Процессор: {txtProcessor.Text}");
                sb.AppendLine($"ОЗУ: {txtRAM.Text}");
                sb.AppendLine($"Ven4Tools: {txtAppVersion.Text}");
                sb.AppendLine();
                sb.AppendLine("--- История перезагрузок и сбоев ---");
                if (_lastRebootDiagnoses.Count == 0)
                {
                    sb.AppendLine("Нештатных завершений работы за последние 7 дней не найдено (или диагностика ещё не запускалась).");
                }
                else
                {
                    foreach (var d in _lastRebootDiagnoses)
                        sb.AppendLine($"[{d.Category}] {d.TimeCreated:g} — {d.Summary} | {d.RawDetails}");
                }
                sb.AppendLine();
                sb.AppendLine("--- Диски ---");
                sb.AppendLine(string.Join(Environment.NewLine,
                    pnlDisks.Children.OfType<TextBlock>().Select(t => t.Text)));
                sb.AppendLine();
                sb.AppendLine("--- Ошибки Windows Update ---");
                sb.AppendLine(string.Join(Environment.NewLine,
                    pnlWindowsUpdateFailures.Children.OfType<TextBlock>().Select(t => t.Text)));
                sb.AppendLine();
                sb.AppendLine("--- Аппаратные и драйверные события ---");
                sb.AppendLine(txtHardwareSummary.Text);
                if (txtHardwareRaw.Visibility == Visibility.Visible)
                    sb.AppendLine(txtHardwareRaw.Text);

                Clipboard.SetText(sb.ToString());
                AppLogger.Write("📤 Полный отчёт диагностики скопирован в буфер обмена");
                MessageBox.Show("✅ Отчёт скопирован в буфер обмена.", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка копирования отчёта: {ex.Message}");
            }
        }
    }
}
