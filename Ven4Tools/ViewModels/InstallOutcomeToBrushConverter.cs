using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Ven4Tools.Models;

namespace Ven4Tools.ViewModels
{
    // Красит текст статуса строки прогресса установки (lstAppProgress в CatalogTab)
    // по итогу сверки результата с фактическим состоянием системы (AppInstallProgress.
    // Outcome), отдельно от InstallPhaseToBrushConverter (тот красит саму полоску
    // прогресса по этапу процесса — Phase). Разделение намеренное: Phase — «на каком
    // мы этапе», Outcome — «насколько мы уверены в итоге». Unconfirmed — отдельный
    // цвет от Error: «не уверены» — это не то же самое, что «точно не получилось»
    // (пользовательское требование 2026-07-24 — не путать эти два состояния).
    public sealed class InstallOutcomeToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // NotYetDetermined — большую часть времени (все промежуточные статусы вроде
            // «Скачивание...», «Установка...» до финального отчёта) — используем тот же
            // нейтральный цвет, что и раньше (TextSecondary), чтобы не красить весь
            // процесс установки без надобности, только финальный итог.
            string resourceKey = value is InstallOutcome outcome
                ? outcome switch
                {
                    InstallOutcome.ConfirmedSuccess => "StatusSuccess",   // подтверждённый успех — зелёный
                    InstallOutcome.AlreadyUpToDate => "StatusInfo",      // уже стояло, не изменилось — синий
                    InstallOutcome.Unconfirmed => "StatusWarning",       // не уверены — янтарный, не путать с Error
                    InstallOutcome.ConfirmedFailure => "StatusDanger",   // точно не получилось — красный
                    _ => "TextSecondary"                                 // NotYetDetermined — нейтральный, как раньше
                }
                : "TextSecondary";

            return Application.Current.TryFindResource(resourceKey) as Brush
                ?? Application.Current.TryFindResource("TextSecondary") as Brush
                ?? Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
