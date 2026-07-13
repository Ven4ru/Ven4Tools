using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;

namespace Ven4Tools.ViewModels
{
    // GroupStyle.HeaderTemplate получает CollectionViewGroup, чьё Name — строка
    // категории. Конвертер превращает её в готовую CategoryHeaderViewModel из
    // словаря, который CatalogViewModel строит при загрузке каталога.
    // Headers выставляется один раз из CatalogTab.xaml.cs после создания
    // CatalogViewModel — обычный способ прокинуть контекст в конвертер, когда
    // DI-контейнера для XAML-ресурсов нет.
    public sealed class CategoryNameToHeaderConverter : IValueConverter
    {
        public IDictionary<string, CategoryHeaderViewModel>? Headers { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string name && Headers != null && Headers.TryGetValue(name, out var vm))
                return vm;
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
