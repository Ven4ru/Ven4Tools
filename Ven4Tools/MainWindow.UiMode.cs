using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Services;
using Ven4Tools.Views.Tabs;

namespace Ven4Tools
{
    /// <summary>
    /// Две оболочки главного окна — новая и прежняя — и переключение между ними.
    ///
    /// Разделы и их кнопки в обоих видах одни и те же: различается только раскладка
    /// бокового меню. В прежней — двенадцать пунктов тремя списками, в новой — шесть:
    /// «Обзор», «Каталог», «Установленные», группы «Windows» и «Сервис», «История»,
    /// а «Настройки» и «О программе» вынесены вниз. Кнопки не дублируются, а
    /// переставляются между панелями, поэтому обработчики, имена для автоматизации и
    /// правила видимости (офлайн скрывает сетевые разделы) работают одинаково.
    /// </summary>
    public partial class MainWindow
    {
        private OverviewTab? _overviewTab;
        private bool _uiModern;
        private Button? _activeNavButton;

        // Исходные места кнопок в прежнем меню — чтобы вернуть их точно туда же.
        private (FrameworkElement Element, int Index)[]? _classicNavOrder;

        private const string ChevronDown = "";
        private const string ChevronUp = "";

        private void ApplyUiMode()
        {
            _uiModern = UiModeService.IsModern;
            _classicNavOrder ??= navClassic.Children.Cast<FrameworkElement>()
                .Select((element, index) => (element, index)).ToArray();

            var movable = new FrameworkElement[]
            {
                btnCatalogTab, btnInstalledTab, btnSystemTab, btnDiagnosticsTab, btnBenchmarkTab,
                gridWindowsUpdateNav, btnOfficeTab, btnActivationTab, btnDebloaterTab,
                btnNetworkTab, btnHistoryTab, btnAboutTab
            };
            foreach (var element in movable)
                (element.Parent as Panel)?.Children.Remove(element);

            if (_uiModern)
            {
                slotCatalog.Children.Add(btnCatalogTab);
                slotInstalled.Children.Add(btnInstalledTab);
                foreach (var element in new FrameworkElement[] { gridWindowsUpdateNav, btnOfficeTab, btnActivationTab, btnDebloaterTab })
                    pnlGroupWindows.Children.Add(element);
                foreach (var element in new FrameworkElement[] { btnNetworkTab, btnDiagnosticsTab, btnBenchmarkTab })
                    pnlGroupService.Children.Add(element);
                slotHistory.Children.Add(btnHistoryTab);
                slotSettings.Children.Add(btnSystemTab);
                slotAbout.Children.Add(btnAboutTab);
            }
            else
            {
                // По возрастанию исходного индекса: подписи списков остались на месте,
                // поэтому каждая кнопка встаёт ровно туда, где была.
                foreach (var (element, index) in _classicNavOrder.OrderBy(x => x.Index))
                {
                    if (element.Parent == null)
                        navClassic.Children.Insert(System.Math.Min(index, navClassic.Children.Count), element);
                }
            }

            navModern.Visibility = _uiModern ? Visibility.Visible : Visibility.Collapsed;
            navClassic.Visibility = _uiModern ? Visibility.Collapsed : Visibility.Visible;

            // Новая оболочка компактнее: уже меню, ниже шапка, в шапке — название раздела.
            colSidebar.Width = new GridLength(_uiModern ? 212 : 228);
            rowHeader.Height = new GridLength(_uiModern ? 48 : 58);
            txtHeaderSubtitle.Visibility = _uiModern ? Visibility.Collapsed : Visibility.Visible;
            if (!_uiModern) txtHeaderTitle.Text = "Рабочее пространство";

            btnUiModeSwitch.Content = _uiModern ? "Старый интерфейс" : "Новый интерфейс";
            btnUiModeSwitch.ToolTip = _uiModern
                ? "Вернуть прежний вид окна: все разделы отдельными пунктами меню. Обратно — этой же кнопкой."
                : "Включить новый вид окна: обзор и шесть пунктов меню. Обратно — этой же кнопкой.";

            _catalogTab?.ApplyUiMode(_uiModern);

            if (_activeNavButton != null) OnNavigated(_activeNavButton);
        }

        /// <summary>Вызывается после смены раздела: шапка и раскрытие группы меню.</summary>
        private void OnNavigated(Button activeButton)
        {
            _activeNavButton = activeButton;
            if (!_uiModern) return;

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

        private void BtnUiModeSwitch_Click(object sender, RoutedEventArgs e)
        {
            UiModeService.Set(!_uiModern);
            ApplyUiMode();
            AppLogger.Write(_uiModern ? "🎛 Включён новый интерфейс" : "🎛 Включён прежний интерфейс");

            // Новый вид открываем с «Обзора» — это и есть главное отличие. В прежнем
            // «Обзора» нет: если он был открыт, уходим в каталог.
            if (_uiModern) NavigateToOverview(null, null);
            else if (_currentTab == "overview") NavigateToCatalog(null, null);
        }
    }
}
