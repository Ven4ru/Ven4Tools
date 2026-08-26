using System;
using System.Windows.Controls;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «Диагностика» — тонкая обёртка над <see cref="DiagnosticsViewModel"/>.
    /// Вся логика перенесена в ViewModel при MVVM-миграции (2026-08-26, восьмая
    /// вкладка после DebloaterTab/HistoryTab/AboutTab/ActivationTab/NetworkTab/
    /// OfficeTab/InstalledTab). Единственный публичный член сверх конструктора —
    /// event GoToWindowsUpdate (внешний контракт, MainWindow.xaml.cs).
    /// </summary>
    public partial class DiagnosticsTab : UserControl
    {
        private readonly DiagnosticsViewModel _viewModel = new();
        private bool _initialized = false;

        public event Action? GoToWindowsUpdate;

        public DiagnosticsTab()
        {
            InitializeComponent();
            DataContext = _viewModel;
            _viewModel.GoToWindowsUpdate += () => GoToWindowsUpdate?.Invoke();

            Loaded += async (_, _) =>
            {
                if (_initialized) return;
                _initialized = true;
                await _viewModel.InitializeAsync();
            };
        }
    }
}
