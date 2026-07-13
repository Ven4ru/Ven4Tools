using System;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    // Тонкая обёртка над CatalogViewModel — вся логика (загрузка каталога, поиск,
    // доступность, установка, пресеты) перенесена в Ven4Tools/ViewModels при
    // переходе на MVVM (2026-07-13, см. audit по добавлению Play-кнопки).
    // Публичный контракт (события/методы/свойства ниже) сохранён без изменений —
    // MainWindow.xaml.cs обращается к ним напрямую.
    public partial class CatalogTab : UserControl
    {
        private readonly CatalogViewModel _viewModel = new();
        private bool _initialized;
        private bool _eventsSubscribed;
        private int _lastSourceOrderVersion = SourceOrderService.Version;
        private Action? _profileChangedHandler;
        private bool _availabilityShutdownHooked;

        public event Action? SwitchToUpdatesRequested
        {
            add => _viewModel.SwitchToUpdatesRequested += value;
            remove => _viewModel.SwitchToUpdatesRequested -= value;
        }

        public bool IsInstalling => _viewModel.IsInstalling;
        public string SelectedInstallDrive => _viewModel.SelectedInstallDrive;

        public CatalogTab()
        {
            InitializeComponent();
            DataContext = _viewModel;
            _viewModel.OwnerWindowProvider = () => Window.GetWindow(this);

            // Прокидываем словарь заголовков категорий в конвертер — обычный способ
            // передать контекст в IValueConverter, когда для XAML-ресурсов нет DI.
            if (Resources["CategoryHeaderConverter"] is CategoryNameToHeaderConverter conv)
                conv.Headers = _viewModel.CategoryHeaders;

            _profileChangedHandler = () => Dispatcher.Invoke(_viewModel.ApplyProfileFilters);

            Loaded += async (s, e) =>
            {
                // Loaded может срабатывать многократно (переключение вкладок) —
                // подписываемся только один раз, иначе обработчики дублируются.
                if (!_eventsSubscribed)
                {
                    ProfileService.Changed     += _profileChangedHandler;
                    AppSettings.Changed        += OnAppSettingsChanged;
                    SourceOrderService.Changed += _viewModel.OnSourceOrderChanged;
                    _eventsSubscribed = true;
                }
                // Отменяем ретраи доступности при закрытии окна (вкладка кэшируется и
                // переиспользуется, поэтому привязываемся к закрытию окна, а не к Unloaded).
                if (!_availabilityShutdownHooked)
                {
                    var window = Window.GetWindow(this);
                    if (window != null)
                    {
                        window.Closed += (_, _) => _viewModel.CancelAvailabilityRetries();
                        _availabilityShutdownHooked = true;
                    }
                }
                if (!_initialized)
                {
                    _initialized = true;
                    await _viewModel.LoadAsync();
                    _lastSourceOrderVersion = SourceOrderService.Version;
                }
                else if (_lastSourceOrderVersion != SourceOrderService.Version)
                {
                    // Порядок источников меняли, пока вкладка была выгружена — запускаем
                    // обещанную перепроверку доступности сейчас, при открытии вкладки.
                    _viewModel.OnSourceOrderChanged();
                    _lastSourceOrderVersion = SourceOrderService.Version;
                }
            };
            Unloaded += (_, _) =>
            {
                if (_eventsSubscribed)
                {
                    ProfileService.Changed     -= _profileChangedHandler;
                    AppSettings.Changed        -= OnAppSettingsChanged;
                    SourceOrderService.Changed -= _viewModel.OnSourceOrderChanged;
                    _eventsSubscribed = false;
                }
            };
        }

        private void OnAppSettingsChanged() => _viewModel.UpdateTimeouts();

        public void AddLocalInstallerApp(AppInfo app) => _viewModel.AddLocalInstallerApp(app);
    }
}
