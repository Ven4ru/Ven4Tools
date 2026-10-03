using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Экран «Обзор» новой оболочки. Вся логика — в <see cref="OverviewViewModel"/>;
    /// здесь только жизненный цикл подписок и проброс просьбы открыть раздел.
    /// </summary>
    public partial class OverviewTab : UserControl
    {
        private readonly OverviewViewModel _viewModel = new();

        /// <summary>Просьба открыть раздел (ключ — как у главного окна).</summary>
        public event Action<string>? NavigateRequested;

        public OverviewTab()
        {
            InitializeComponent();
            DataContext = _viewModel;
            _viewModel.NavigateRequested += key => NavigateRequested?.Invoke(key);
            Loaded += (_, _) => _viewModel.Attach();
            Unloaded += (_, _) => _viewModel.Detach();
        }
    }

    /// <summary>true → скрыто, false → показано.</summary>
    public sealed class InverseBoolToVisibility : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
