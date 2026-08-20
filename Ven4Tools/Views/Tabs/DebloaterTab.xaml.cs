using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «Очистка» — тонкая обёртка над <see cref="DebloaterViewModel"/>.
    /// Вся логика перенесена в ViewModel при переходе на MVVM (2026-08-21, пилот
    /// перед остальными вкладками). Публичный контракт (три метода ниже) сохранён
    /// без изменений — <c>SystemTab.Snapshots.cs</c> обращается к ним напрямую.
    /// </summary>
    public partial class DebloaterTab : UserControl
    {
        private readonly DebloaterViewModel _viewModel = new();

        public DebloaterTab()
        {
            InitializeComponent();
            DataContext = _viewModel;
            _viewModel.OwnerWindowProvider = () => Window.GetWindow(this);
        }

        // Единственная логика, оставшаяся в code-behind: RadioButton.Checked не
        // биндится напрямую (GroupName делает их взаимоисключающими через сам WPF,
        // а не через ViewModel), поэтому читаем состояние трёх именованных элементов,
        // как делал исходный GetFilteredItems().
        //
        // rbAll.IsChecked="True" в XAML вызывает Checked синхронно ВНУТРИ
        // InitializeComponent() — раньше, чем rbApps/rbPrivacy/rbServices (объявлены
        // в разметке позже) успевают подключиться, отсюда NullReferenceException при
        // каждом первом открытии вкладки (тот же класс WPF-гонки, что у
        // Slider.ValueChanged, см. agent_context.md §7). Оригинальный ApplyFilter()
        // защищался от этого же момента проверкой `if (lstDebloat == null) return;` —
        // здесь lstDebloat заменён на ViewModel, но гонка со сборкой самого XAML
        // никуда не делась, поэтому guard переносится сюда явно.
        private void FilterChanged(object sender, RoutedEventArgs e)
        {
            if (rbApps == null) return;

            if (rbApps.IsChecked == true) _viewModel.CategoryFilter = "app";
            else if (rbPrivacy.IsChecked == true) _viewModel.CategoryFilter = "privacy";
            else if (rbServices.IsChecked == true) _viewModel.CategoryFilter = "service";
            else _viewModel.CategoryFilter = "all";
        }

        // ── Публичный доступ для снапшотов конфигурации (SystemTab.Snapshots.cs) ──

        public IReadOnlyList<string> GetSelectedTweakIds() => _viewModel.GetSelectedTweakIds();

        public void SetSelectedTweakIds(IReadOnlyCollection<string> ids) => _viewModel.SetSelectedTweakIds(ids);

        public Task<(int Succeeded, int Total)> ApplyTweaksByIdsAsync(
            IReadOnlyCollection<string> ids,
            IProgress<string>? progress = null,
            CancellationToken ct = default) =>
            _viewModel.ApplyTweaksByIdsAsync(ids, progress, ct);
    }
}
