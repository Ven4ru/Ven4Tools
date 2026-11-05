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

        /// <summary>
        /// Окно 1280×900 на экране высотой 900 (ноутбуки 1280×800/900, ВМ) уходило низом под
        /// панель задач, и «Центр активности» вместе с нижним краем разделов был не виден.
        /// Размер по умолчанию ужимается до рабочей области, но не меньше MinWidth/MinHeight.
        /// </summary>
        private void FitToWorkArea()
        {
            var area = SystemParameters.WorkArea;
            if (Width > area.Width) Width = System.Math.Max(MinWidth, area.Width);
            if (Height > area.Height) Height = System.Math.Max(MinHeight, area.Height);
        }

        /// <summary>Вызывается после смены раздела: шапка и раскрытие группы меню.</summary>
        private void OnNavigated(Button activeButton)
        {
            string title = activeButton.Content as string ?? "Ven4Tools";
            Button? group = null;
            if (activeButton.Parent == pnlGroupWindows || activeButton == btnWindowsUpdateTab) group = btnGroupWindows;
            else if (activeButton.Parent == pnlGroupService) group = btnGroupService;
            SetHeaderPath(group?.Content as string, title);
            if (group == btnGroupWindows) ExpandGroup(pnlGroupWindows);
            else if (group == btnGroupService) ExpandGroup(pnlGroupService);
        }

        /// <summary>
        /// Шапка показывает путь, а не повтор крупного заголовка раздела: «Windows › Очистка»,
        /// «Сервис › Сеть». Группа — приглушённым цветом, раздел — основным; у разделов вне
        /// групп остаётся одно название. Подписи берутся с кнопок меню — значит, уже в
        /// языке интерфейса, новых строк перевода нет.
        /// </summary>
        private void SetHeaderPath(string? group, string title)
        {
            txtHeaderTitle.Inlines.Clear();
            if (!string.IsNullOrEmpty(group))
            {
                var muted = new System.Windows.Documents.Run(group + "  ›  ");
                muted.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextSecondary");
                muted.FontWeight = FontWeights.Normal;
                txtHeaderTitle.Inlines.Add(muted);
            }
            txtHeaderTitle.Inlines.Add(new System.Windows.Documents.Run(title));
            // Для экранного диктора и UI-проверок имя шапки — по-прежнему название раздела.
            System.Windows.Automation.AutomationProperties.SetName(txtHeaderTitle, title);
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
