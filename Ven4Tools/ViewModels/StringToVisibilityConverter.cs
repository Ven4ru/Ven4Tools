using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Ven4Tools.ViewModels
{
    // BooleanToVisibilityConverter для строкового статуса (например
    // CatalogViewModel.SuggestionsStatus) всегда возвращает Collapsed, потому что
    // вход — не bool. Пустая/null строка — Collapsed, любая другая — Visible.
    public sealed class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
