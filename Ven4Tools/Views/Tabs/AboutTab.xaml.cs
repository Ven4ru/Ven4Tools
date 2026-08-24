using System.Windows.Controls;
using Ven4Tools.Services;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «О программе» — тонкая обёртка над <see cref="AboutViewModel"/>.
    /// Вся логика перенесена в ViewModel при MVVM-миграции (2026-08-25, третья
    /// вкладка после пилота DebloaterTab и HistoryTab). Публичного контракта
    /// сверх конструктора нет — снаружи никто не обращается к AboutTab, кроме
    /// MainWindow.xaml.cs, который только создаёт экземпляр.
    /// </summary>
    public partial class AboutTab : UserControl
    {
        private readonly AboutViewModel _viewModel = new();
        private bool _catalogReadySubscribed;

        public AboutTab()
        {
            InitializeComponent();
            DataContext = _viewModel;

            Loaded += (_, _) =>
            {
                // Loaded может срабатывать многократно (переключение вкладок) —
                // подписываемся только один раз, иначе обработчики дублируются.
                if (!_catalogReadySubscribed)
                {
                    CatalogLoaderService.CatalogReady += OnCatalogReady;
                    _catalogReadySubscribed = true;
                }
                // Обновляем changelog если каталог уже был загружен до открытия вкладки
                _viewModel.RefreshChangelog();
            };
            Unloaded += (_, _) =>
            {
                if (_catalogReadySubscribed)
                {
                    CatalogLoaderService.CatalogReady -= OnCatalogReady;
                    _catalogReadySubscribed = false;
                }
            };
        }

        private void OnCatalogReady(Models.MasterCatalog _)
        {
            Dispatcher.Invoke(() => _viewModel.RefreshChangelog());
        }
    }
}
