using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class DiagnosticsTab : UserControl
    {
        // Итоговый статус-бейдж собирается из результатов всех проверок —
        // эти два флага накапливаются при каждом запуске "Запустить диагностику"
        // (см. также disks/WU-часть в DiagnosticsTab.Report.cs, Task 6-7).
        private bool _lastRunHadCritical;
        private bool _lastRunHadWarning;

        private async Task<List<RebootDiagnosis>> RunRebootHistoryCheckAsync()
        {
            pnlRebootHistory.Children.Clear();

            List<RebootDiagnosis> diagnoses;
            try
            {
                diagnoses = await SystemHealthService.GetRebootHistoryAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsTab.RunRebootHistoryCheckAsync");
                pnlRebootHistory.Children.Add(new TextBlock
                {
                    Text = "Недоступно: не удалось прочитать журнал событий.",
                    Foreground = (Brush)FindResource("StatusWarning")
                });
                return new List<RebootDiagnosis>();
            }

            if (diagnoses.Count == 0)
            {
                pnlRebootHistory.Children.Add(new TextBlock
                {
                    Text = "За последние 7 дней нештатных завершений работы не найдено.",
                    Foreground = (Brush)FindResource("StatusSuccess")
                });
                return diagnoses;
            }

            bool anyFastStartupFailure = false;
            foreach (var d in diagnoses)
            {
                if (d.Category == RebootCategory.Bsod) _lastRunHadCritical = true;
                else _lastRunHadWarning = true;
                if (d.Category == RebootCategory.FastStartupFailure) anyFastStartupFailure = true;

                pnlRebootHistory.Children.Add(BuildRebootCard(d));
            }

            // Кнопку фикса показываем, только если быстрый запуск сейчас
            // действительно включён (или статус не удалось определить) —
            // иначе предлагали бы отключить то, что уже выключено (пользователь
            // мог сам исправить это между запусками диагностики).
            if (anyFastStartupFailure && SystemHealthService.IsFastStartupEnabled() != false)
            {
                var fixBtn = new Button
                {
                    Content = "🔧 Отключить быстрый запуск",
                    Height = 32,
                    Width = 240,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 10, 0, 0)
                };
                fixBtn.Click += BtnDisableFastStartup_Click;
                pnlRebootHistory.Children.Add(fixBtn);
            }

            return diagnoses;
        }

        private UIElement BuildRebootCard(RebootDiagnosis d)
        {
            string icon = d.Category switch
            {
                RebootCategory.Bsod => "🔴",
                RebootCategory.FastStartupFailure => "🟡",
                RebootCategory.PossiblePowerLoss => "🟡",
                _ => "⚪"
            };

            var expander = new Expander
            {
                Header = $"{icon} {d.TimeCreated:g} — {d.Summary}",
                Margin = new Thickness(0, 0, 0, 6)
            };
            expander.Content = new TextBox
            {
                Text = d.RawDetails,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                Background = (Brush)FindResource("CardBackground"),
                Foreground = (Brush)FindResource("TextPrimary"),
                Margin = new Thickness(10, 4, 10, 8)
            };
            return expander;
        }

        private async void BtnDisableFastStartup_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Отключить «Быстрый запуск»? Это уберёт файл гибернации и механизм резюме — «Завершение работы» станет полным холодным выключением.",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                await SystemHealthService.DisableFastStartupAsync();
                AppLogger.Write("🔧 Быстрый запуск отключён");
                MessageBox.Show("✅ Быстрый запуск отключён.", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка при отключении быстрого запуска: {ex.Message}");
                MessageBox.Show("Не удалось отключить быстрый запуск. Запустите приложение от имени администратора и попробуйте ещё раз.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
