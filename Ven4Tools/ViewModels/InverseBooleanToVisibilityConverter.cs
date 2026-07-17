using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Ven4Tools.ViewModels
{
    // Для кнопки "Установить" на карточке приложения — видна, когда
    // IsInstalled == false (т.е. обратно основному BooleanToVisibilityConverter).
    public sealed class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is bool b && !b ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
