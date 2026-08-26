using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Services;
using Ven4Tools.Shared;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «Настройки» — тонкая обёртка над <see cref="SystemViewModel"/>.
    /// Вся логика перенесена в ViewModel при MVVM-миграции (2026-08-26, девятая
    /// вкладка после Debloater/History/About/Activation/Network/Office/Installed/
    /// Diagnostics). Три делегата (OwnerWindowProvider/DebloaterTabProvider/
    /// RefreshTabVisibility) и подписки на события ThemeApplied/
    /// ConnectivityStatusUpdated/CacheLogAppended — единственное, что остаётся
    /// здесь, потому что требует живой Window/UIElement, которого у VM нет.
    /// </summary>
    public partial class SystemTab : UserControl
    {
        private readonly SystemViewModel _viewModel = new();
        private bool _initialized = false;
        private bool _connSubscribed = false;

        public SystemTab()
        {
            InitializeComponent();
            DataContext = _viewModel;

            _viewModel.OwnerWindowProvider = () => Window.GetWindow(this);
            _viewModel.DebloaterTabProvider = () => Window.GetWindow(this) is MainWindow mw ? mw.EnsureDebloaterTab() : null;
            _viewModel.RefreshTabVisibility = () => { if (Window.GetWindow(this) is MainWindow mw) mw.UpdateTabVisibility(); };

            _viewModel.ThemeApplied += () => MotionService.CrossFade((UIElement?)Window.GetWindow(this) ?? this, 220);
            _viewModel.ConnectivityStatusUpdated += () => MotionService.Pulse(pnlConnStatus, 1.015, 160);
            _viewModel.CacheLogAppended += () => txtCacheLog.ScrollToEnd();

            Loaded += SystemTab_Loaded;
            Unloaded += SystemTab_Unloaded;
        }

        private void OnConnectivityChanged(bool online) => Dispatcher.Invoke(_viewModel.UpdateConnectivityStatus);

        private void SystemTab_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_connSubscribed)
            {
                ConnectivityMonitor.StatusChanged -= OnConnectivityChanged;
                _connSubscribed = false;
            }
        }

        private void SystemTab_Loaded(object sender, RoutedEventArgs e)
        {
            // Переподписка при каждом показе вкладки (после Unloaded подписка снимается)
            if (!_connSubscribed)
            {
                ConnectivityMonitor.StatusChanged += OnConnectivityChanged;
                _connSubscribed = true;
            }
            _viewModel.UpdateConnectivityStatus();

            if (_initialized) return;
            _initialized = true;

            _viewModel.Initialize();
        }
    }
}
