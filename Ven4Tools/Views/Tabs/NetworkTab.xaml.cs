using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «Сеть» — тонкая обёртка над <see cref="NetworkViewModel"/>.
    /// Вся логика перенесена в ViewModel при MVVM-миграции (2026-08-25, пятая
    /// вкладка после DebloaterTab/HistoryTab/AboutTab/ActivationTab). Публичного
    /// контракта сверх конструктора нет.
    /// </summary>
    public partial class NetworkTab : System.Windows.Controls.UserControl
    {
        private readonly NetworkViewModel _viewModel = new();

        public NetworkTab()
        {
            InitializeComponent();
            DataContext = _viewModel;

            Loaded += (_, _) => _viewModel.RefreshAdaptersCommand.Execute(null);
        }
    }
}
