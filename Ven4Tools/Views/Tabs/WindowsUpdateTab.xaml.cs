using System;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «Обновления Windows» — тонкая обёртка над <see cref="WindowsUpdateViewModel"/>.
    /// Вся логика перенесена в ViewModel при MVVM-миграции (2026-08-26, десятая
    /// вкладка после Debloater/History/About/Activation/Network/Office/Installed/
    /// Diagnostics/System). Единственный публичный член сверх конструктора —
    /// event GoToDiagnostics (внешний контракт, MainWindow.xaml.cs).
    /// </summary>
    public partial class WindowsUpdateTab : UserControl
    {
        private readonly WindowsUpdateViewModel _viewModel = new();
        private bool _firstRunHandled;

        public event Action? GoToDiagnostics;

        public WindowsUpdateTab()
        {
            InitializeComponent();
            DataContext = _viewModel;
            _viewModel.OwnerWindowProvider = () => Window.GetWindow(this);
            _viewModel.GoToDiagnostics += () => GoToDiagnostics?.Invoke();

            Loaded += (_, _) => TabInitGuard.RunOnce(
                ref _firstRunHandled, _viewModel.InitializeAsync,
                "[WindowsUpdateTab] Ошибка инициализации вкладки");
        }
    }
}
