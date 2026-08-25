using System.Windows.Controls;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «Установленные» — тонкая обёртка над <see cref="InstalledViewModel"/>.
    /// Вся логика перенесена в ViewModel при MVVM-миграции (2026-08-26, седьмая
    /// вкладка после DebloaterTab/HistoryTab/AboutTab/ActivationTab/NetworkTab/
    /// OfficeTab). Публичные члены сверх конструктора — внешний контракт:
    /// MainWindow.xaml.cs вызывает StartPreload() до создания вкладки и
    /// ShowUpdatesFilter() на уже созданном экземпляре.
    /// </summary>
    public partial class InstalledTab : UserControl
    {
        private readonly InstalledViewModel _viewModel = new();

        public InstalledTab()
        {
            InitializeComponent();
            DataContext = _viewModel;
            Loaded += (_, _) => _ = _viewModel.LoadAppsAsync();
        }

        public static void StartPreload() => InstalledViewModel.StartPreload();

        public void ShowUpdatesFilter() => _viewModel.ShowUpdatesFilter();
    }
}
