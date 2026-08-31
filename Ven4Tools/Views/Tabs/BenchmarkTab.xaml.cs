using System.Windows.Controls;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «Бенчмарк» — тонкая обёртка над <see cref="BenchmarkViewModel"/>.
    /// Вся логика перенесена в ViewModel при MVVM-миграции (2026-08-26, одиннадцатая
    /// и последняя вкладка серии — после неё клиент Ven4Tools полностью на MVVM).
    /// </summary>
    public partial class BenchmarkTab : UserControl
    {
        private readonly BenchmarkViewModel _viewModel = new();
        private bool _initialized;

        public BenchmarkTab()
        {
            InitializeComponent();
            DataContext = _viewModel;

            Loaded += (_, _) => TabInitGuard.RunOnce(
                ref _initialized, _viewModel.InitializeAsync,
                "BenchmarkTab.InitializeAsync");
        }
    }
}
