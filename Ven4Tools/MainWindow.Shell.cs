using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Services;
using Ven4Tools.Views.Tabs;

namespace Ven4Tools
{
    /// <summary>
    /// Боковое меню главного окна: шесть пунктов — «Обзор», «Каталог», «Установленные»,
    /// группы «Windows» и «Сервис», «История», а «Настройки» и «О программе» вынесены
    /// вниз. Здесь же — раскрытие групп, шапка с названием раздела и переходы с «Обзора».
    /// </summary>
    public partial class MainWindow
    {
        private OverviewTab? _overviewTab;

        private const string ChevronDown = "";
        private const string ChevronUp = "";

        /// <summary>Вызывается после смены раздела: шапка и раскрытие группы меню.</summary>
        private void OnNavigated(Button activeButton)
        {
            txtHeaderTitle.Text = activeButton.Content as string ?? "Ven4Tools";
            if (activeButton.Parent == pnlGroupWindows || activeButton == btnWindowsUpdateTab) ExpandGroup(pnlGroupWindows);
            else if (activeButton.Parent == pnlGroupService) ExpandGroup(pnlGroupService);
        }

        private void ExpandGroup(StackPanel? group)
        {
            pnlGroupWindows.Visibility = group == pnlGroupWindows ? Visibility.Visible : Visibility.Collapsed;
            pnlGroupService.Visibility = group == pnlGroupService ? Visibility.Visible : Visibility.Collapsed;
            chevGroupWindows.Text = group == pnlGroupWindows ? ChevronUp : ChevronDown;
            chevGroupService.Text = group == pnlGroupService ? ChevronUp : ChevronDown;
        }

        private void BtnGroupWindows_Click(object sender, RoutedEventArgs e) =>
            ExpandGroup(pnlGroupWindows.Visibility == Visibility.Visible ? null : pnlGroupWindows);

        private void BtnGroupService_Click(object sender, RoutedEventArgs e) =>
            ExpandGroup(pnlGroupService.Visibility == Visibility.Visible ? null : pnlGroupService);

        private void NavigateToOverview(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnOverviewTab);
            if (sender != null) AppLogger.Write("📂 Открыта вкладка: Обзор");
            if (_overviewTab == null)
            {
                _overviewTab = new OverviewTab();
                _overviewTab.NavigateRequested += OpenFromOverview;
            }
            MainFrame.Content = _overviewTab;
            ExpandGroup(null);
            UpdateMascot("overview");
        }

        private void OpenFromOverview(string key)
        {
            if (key.StartsWith(ViewModels.OverviewViewModel.ReadySetPrefix, System.StringComparison.Ordinal))
            {
                var set = ReadySets.Find(key.Substring(ViewModels.OverviewViewModel.ReadySetPrefix.Length));
                if (set == null) return;
                NavigateToCatalog(this, null);
                _catalogTab!.ApplyAppSet(set.AppIds, set.Title);
                return;
            }

            switch (key)
            {
                case "catalog": NavigateToCatalog(this, null); break;
                case "updates":
                    _installedTab ??= new InstalledTab();
                    _installedTab.ShowUpdatesFilter();
                    NavigateToInstalled(this, null);
                    break;
                case "windowsupdate": NavigateToWindowsUpdate(this, null); break;
                case "debloater": NavigateToDebloater(this, null); break;
                case "diagnostics": NavigateToDiagnostics(this, null); break;
                case "history": NavigateToHistory(this, null); break;
                case "about": NavigateToAbout(this, null); break;
            }
        }
    }
}
