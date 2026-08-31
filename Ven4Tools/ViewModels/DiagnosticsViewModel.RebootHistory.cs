using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Helpers;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class DiagnosticsViewModel
    {
        // Итоговый статус-бейдж собирается из результатов всех проверок —
        // эти два флага накапливаются при каждом запуске "Запустить диагностику"
        // (см. также disks/WU-часть в DiagnosticsViewModel.Checks.cs).
        private bool _lastRunHadCritical;
        private bool _lastRunHadWarning;

        private async Task<List<RebootDiagnosis>> RunRebootHistoryCheckAsync()
        {
            RebootStatusRow = null;
            ShowRebootStatusRow = false;
            RebootCards = Array.Empty<RebootCardInfo>();
            ShowDisableFastStartupButton = false;

            List<RebootDiagnosis> diagnoses;
            try
            {
                diagnoses = await SystemHealthService.GetRebootHistoryAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.RunRebootHistoryCheckAsync");
                RebootStatusRow = new DiagnosticsTextRow { Text = "Недоступно: не удалось прочитать журнал событий.", Foreground = BrushResolver.Resolve("StatusWarning") };
                ShowRebootStatusRow = true;
                return new List<RebootDiagnosis>();
            }

            if (diagnoses.Count == 0)
            {
                RebootStatusRow = new DiagnosticsTextRow { Text = "За последние 7 дней нештатных завершений работы не найдено.", Foreground = BrushResolver.Resolve("StatusSuccess") };
                ShowRebootStatusRow = true;
                return diagnoses;
            }

            bool anyFastStartupFailure = false;
            var cards = new List<RebootCardInfo>();
            foreach (var d in diagnoses)
            {
                if (d.Category == RebootCategory.Bsod) _lastRunHadCritical = true;
                else _lastRunHadWarning = true;
                if (d.Category == RebootCategory.FastStartupFailure) anyFastStartupFailure = true;

                cards.Add(BuildRebootCard(d));
            }

            RebootCards = cards;

            // Кнопку фикса показываем, только если быстрый запуск сейчас
            // действительно включён (или статус не удалось определить) —
            // иначе предлагали бы отключить то, что уже выключено (пользователь
            // мог сам исправить это между запусками диагностики).
            ShowDisableFastStartupButton = anyFastStartupFailure && SystemHealthService.IsFastStartupEnabled() != false;

            return diagnoses;
        }

        private static RebootCardInfo BuildRebootCard(RebootDiagnosis d)
        {
            string icon = d.Category switch
            {
                RebootCategory.Bsod => "🔴",
                RebootCategory.FastStartupFailure => "🟡",
                RebootCategory.PossiblePowerLoss => "🟡",
                _ => "⚪"
            };

            return new RebootCardInfo
            {
                Header = $"{icon} {d.TimeCreated:g} — {d.Summary}",
                RawDetails = d.RawDetails
            };
        }

        private async Task RunDisableFastStartupAsync()
        {
            // Гейт реентерабельности — см. пояснение в RunDisableTurboBoostAsync:
            // без него двойное нажатие запускало два powercfg с правами администратора
            // одновременно. Флаг взводится до диалога подтверждения, иначе второе
            // нажатие успевало открыть второе такое же окно.
            if (IsDisablingFastStartup) return;
            IsDisablingFastStartup = true;
            try
            {
                var confirm = MessageBox.Show(
                    "Отключить «Быстрый запуск»? Это уберёт файл гибернации и механизм резюме — «Завершение работы» станет полным холодным выключением.",
                    "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;

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
            finally
            {
                IsDisablingFastStartup = false;
            }
        }
    }
}
