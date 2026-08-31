using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Helpers;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class DiagnosticsViewModel
    {
        private List<RebootDiagnosis> _lastRebootDiagnoses = new();

        private async Task RunDiagnosticsAsync()
        {
            if (IsRunningDiagnostics) return;

            IsRunningDiagnostics = true;
            HealthBadgeText = "Диагностика выполняется...";
            HealthBadgeBrush = BrushResolver.Resolve("TextSecondary");
            _lastRunHadCritical = false;
            _lastRunHadWarning = false;
            ShowPlaceholders = false;

            try
            {
                _lastRebootDiagnoses = await RunRebootHistoryCheckAsync();
                await RunDiskCheckAsync();
                await RunWindowsUpdateCheckAsync();
                await RunHardwareEventsCheckAsync();

                if (_lastRunHadCritical)
                {
                    HealthBadgeText = "🔴 Критично — есть находки, требующие внимания";
                    HealthBadgeBrush = BrushResolver.Resolve("StatusDanger");
                }
                else if (_lastRunHadWarning)
                {
                    HealthBadgeText = "🟡 Есть на что посмотреть";
                    HealthBadgeBrush = BrushResolver.Resolve("StatusWarning");
                }
                else
                {
                    HealthBadgeText = "🟢 Всё в порядке";
                    HealthBadgeBrush = BrushResolver.Resolve("StatusSuccess");
                }
                LastRunText = $"Последний запуск: {DateTime.Now:g}";
                AppLogger.Write("🔍 Диагностика ПК выполнена");
            }
            finally
            {
                IsRunningDiagnostics = false;
            }
        }

        private void CopyFullReport()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("=== Отчёт диагностики Ven4Tools ===");
                sb.AppendLine($"Время: {DateTime.Now:g}");
                sb.AppendLine();
                sb.AppendLine($"ОС: {OSVersionText}");
                sb.AppendLine($"Процессор: {ProcessorText}");
                sb.AppendLine($"ОЗУ: {RAMText}");
                sb.AppendLine($"Ven4Tools: {AppVersionText}");
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
                // ShowPlaceholders — диагностика ещё не запускалась: оригинал собирал этот
                // раздел из живых дочерних TextBlock, которые до первого запуска содержат
                // текст плейсхолдера «Нажмите «Запустить диагностику»» — воспроизводим то же.
                sb.AppendLine(ShowPlaceholders
                    ? "Нажмите «Запустить диагностику»"
                    : string.Join(Environment.NewLine, DiskRows.Select(r => r.Text)));
                sb.AppendLine();
                sb.AppendLine("--- Ошибки Windows Update ---");
                sb.AppendLine(ShowPlaceholders
                    ? "Нажмите «Запустить диагностику»"
                    : string.Join(Environment.NewLine, WuRows.Select(r => r.Text)));
                sb.AppendLine();
                sb.AppendLine("--- Аппаратные и драйверные события ---");
                sb.AppendLine(HardwareSummaryText);
                if (HardwareRawVisible)
                    sb.AppendLine(HardwareRawText);

                Clipboard.SetText(sb.ToString());
                AppLogger.Write("📤 Полный отчёт диагностики скопирован в буфер обмена");
                MessageBox.Show("✅ Отчёт скопирован в буфер обмена.", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.CopyFullReport");
                MessageBox.Show("Не удалось скопировать отчёт: " + ex.Message, "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
