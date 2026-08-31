using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Helpers;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class DiagnosticsViewModel
    {
        private async Task RunDiskCheckAsync()
        {
            DiskRows = Array.Empty<DiagnosticsTextRow>();
            try
            {
                var disks = await SystemHealthService.GetDiskHealthAsync();
                if (disks.Count == 0)
                {
                    DiskRows = new[] { new DiagnosticsTextRow { Text = "Диски не найдены.", Foreground = BrushResolver.Resolve("TextSecondary") } };
                    return;
                }
                var rows = new List<DiagnosticsTextRow>();
                foreach (var disk in disks)
                {
                    if (disk.Health is DiskHealth.Warning or DiskHealth.Unhealthy) _lastRunHadCritical = true;
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
                    rows.Add(new DiagnosticsTextRow
                    {
                        Text = $"{icon} {disk.Name} — {label}",
                        Foreground = BrushResolver.Resolve("TextPrimary")
                    });
                }
                DiskRows = rows;
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.RunDiskCheckAsync");
                DiskRows = new[] { new DiagnosticsTextRow { Text = "Недоступно: не удалось получить состояние дисков.", Foreground = BrushResolver.Resolve("StatusWarning") } };
            }
        }

        private async Task RunWindowsUpdateCheckAsync()
        {
            WuRows = Array.Empty<DiagnosticsTextRow>();
            WuButtonsVisible = false;
            try
            {
                var failures = await SystemHealthService.GetWindowsUpdateFailuresAsync();
                if (failures.Count == 0)
                {
                    WuRows = new[] { new DiagnosticsTextRow { Text = "За последние 7 дней ошибок обновления Windows не найдено.", Foreground = BrushResolver.Resolve("StatusSuccess") } };
                    return;
                }

                _lastRunHadWarning = true;
                WuRows = failures.Take(20)
                    .Select(f => new DiagnosticsTextRow { Text = $"🟡 {f.TimeCreated:g} — {f.Message}", Foreground = BrushResolver.Resolve("TextPrimary") })
                    .ToList();
                // Ошибки есть — предлагаем сразу перейти туда, где патчи можно
                // переустановить, не заставляя искать вкладку в меню вручную.
                WuButtonsVisible = true;
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.RunWindowsUpdateCheckAsync");
                WuRows = new[] { new DiagnosticsTextRow { Text = "Недоступно: не удалось прочитать журнал Windows Update.", Foreground = BrushResolver.Resolve("StatusWarning") } };
            }
        }

        private async Task RunClearWuCacheAsync()
        {
            if (IsClearingWuCache) return;

            var confirm = MessageBox.Show(
                "Остановить службы обновления Windows и очистить кэш загрузки? Службы будут перезапущены автоматически.",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            IsClearingWuCache = true;
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
                IsClearingWuCache = false;
            }
        }

        private async Task RunHardwareEventsCheckAsync()
        {
            try
            {
                var summary = await SystemHealthService.GetHardwareEventsAsync();
                HardwareSummaryText =
                    $"Аппаратных ошибок (WHEA): {summary.WheaCount}. Сбоев видеодрайвера: {summary.DisplayDriverCrashCount}.";

                if (summary.RawEntries.Count > 0)
                {
                    HardwareRawText = string.Join(Environment.NewLine, summary.RawEntries);
                    HardwareRawVisible = true;
                }
                else
                {
                    HardwareRawVisible = false;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.RunHardwareEventsCheckAsync");
                HardwareSummaryText = "Недоступно: не удалось прочитать аппаратные события.";
            }
        }
    }
}
