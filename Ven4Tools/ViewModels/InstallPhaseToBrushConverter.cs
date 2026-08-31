using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Ven4Tools.Helpers;
using Ven4Tools.Models;

namespace Ven4Tools.ViewModels
{
    // Красит полоску прогресса установки по фазе (AppInstallProgress.Phase), чтобы
    // «Загрузка» и «Установка» визуально различались (пользовательский фидбог
    // 2026-07-24: единая полоска одного цвета не давала понять, на каком этапе
    // процесс). Используются уже существующие ключи палитры — те же, что красят
    // статусные индикаторы в MainWindow/DiagnosticsTab (StatusSuccess/StatusDanger/
    // StatusWarning), а не новая произвольная палитра.
    public sealed class InstallPhaseToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string resourceKey = value is InstallPhase phase
                ? phase switch
                {
                    InstallPhase.Download => "StatusInfo",     // скачивание — синий
                    InstallPhase.Installing => "AccentColor",  // установка — акцент темы
                    InstallPhase.Done => "AccentColor",        // готово — тот же акцент
                    InstallPhase.Error => "StatusDanger",      // ошибка/отмена — красный
                    _ => "AccentColor"
                }
                : "AccentColor";

            // Ключи ищутся через TryFindResource, чтобы не падать, если словарь
            // ресурсов почему-то не подключён (юнит-тесты, дизайнер).
            // Фолбэк серый, а не белый как у ViewModel-ей: это цвет самой полоски прогресса.
            return BrushResolver.Resolve(resourceKey, "AccentColor", Brushes.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
