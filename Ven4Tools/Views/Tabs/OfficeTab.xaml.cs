using System;
using System.Windows.Controls;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «Office» — тонкая обёртка над <see cref="OfficeViewModel"/>.
    /// Вся логика перенесена в ViewModel при MVVM-миграции (2026-08-25, шестая
    /// вкладка после DebloaterTab/HistoryTab/AboutTab/ActivationTab/NetworkTab).
    /// Единственный публичный член сверх конструктора — <see cref="GoToActivation"/>,
    /// внешний контракт: MainWindow.xaml.cs подписывается на него напрямую.
    /// </summary>
    public partial class OfficeTab : UserControl
    {
        private readonly OfficeViewModel _viewModel = new();

        public event Action? GoToActivation;

        public OfficeTab()
        {
            InitializeComponent();
            DataContext = _viewModel;
            _viewModel.GoToActivation += () => GoToActivation?.Invoke();
        }
    }
}
