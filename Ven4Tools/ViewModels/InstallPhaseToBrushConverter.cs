using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Ven4Tools.Models;

namespace Ven4Tools.ViewModels
{
    // Красит полоску прогресса установки по фазе (AppInstallProgress.Phase), чтобы
    // «Загрузка» и «Установка» визуально различались (пользовательский фидбог
    // 2026-07-24: единая полоска одного цвета не давала понять, на каком этапе
    // процесс). Используются уже существующие в проекте статичные кисти палитры
    // (Shared/DesignTokens.xaml) — те же, что красят статусные индикаторы в
    // MainWindow/DiagnosticsTab (StatusSuccess/StatusDanger/StatusWarning), а не
    // новая произвольная палитра.
    public sealed class InstallPhaseToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string resourceKey = value is InstallPhase phase
                ? phase switch
                {
                    InstallPhase.Download => "StatusInfo",     // скачивание — синий
                    InstallPhase.Installing => "BrandGreen",   // установка — зелёный
                    InstallPhase.Done => "BrandGreen",         // готово — тот же зелёный
                    InstallPhase.Error => "StatusDanger",      // ошибка/отмена — красный
                    _ => "AccentColor"
                }
                : "AccentColor";

            // StaticResource-кисти из DesignTokens.xaml не зависят от темы (в отличие
            // от AccentColor, который ThemeService переопределяет на лету) — ищем через
            // TryFindResource, чтобы не падать, если ресурс почему-то не подключён.
            return Application.Current.TryFindResource(resourceKey) as Brush
                ?? Application.Current.TryFindResource("AccentColor") as Brush
                ?? Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
