using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Экран «Обзор». Вся логика — в <see cref="OverviewViewModel"/>;
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
            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(OverviewViewModel.AppUpdatesNeedAttention)
                                   or nameof(OverviewViewModel.WindowsUpdateNeedsAttention))
                {
                    // Сводка может пересчитываться из фонового потока — стиль ставим в потоке окна.
                    if (Dispatcher.CheckAccess()) ApplyActionEmphasis();
                    else Dispatcher.BeginInvoke((Action)ApplyActionEmphasis);
                }
            };
            ApplyActionEmphasis();
        }

        /// <summary>
        /// Кнопка карточки, которая требует внимания (жёлтая рамка), становится главной
        /// (<c>PrimaryButtonStyle</c>); остальное время — обычной. Стиль меняется целиком:
        /// триггер внутри стиля не может подменить сам стиль, а набор отдельных цветов
        /// разошёлся бы с поведением Primary (затемнение при наведении, выключенный вид).
        /// </summary>
        private void ApplyActionEmphasis()
        {
            var normal = (Style)FindResource("OverviewAction");
            var primary = (Style)FindResource("OverviewActionPrimary");
            btnOverviewAppUpdates.Style = _viewModel.AppUpdatesNeedAttention ? primary : normal;
            btnOverviewWindowsUpdate.Style = _viewModel.WindowsUpdateNeedsAttention ? primary : normal;
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
