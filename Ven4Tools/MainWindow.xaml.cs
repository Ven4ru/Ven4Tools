using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ven4Tools.Views.Tabs;

namespace Ven4Tools
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            
            if (!IsRunAsAdmin())
            {
                RestartAsAdmin();
                return;
            }
            
            NavigateToCatalog(null, null);
            
            btnThemeToggle.IsChecked = false;
        }
        private void InitializeButtons()
{
    btnNetworkTab = new Button();
}
        private void NavigateToNetwork(object? sender, RoutedEventArgs? e)
{
    SetActiveButton(btnNetworkTab);
    MainFrame.Navigate(new NetworkTab());
}
        private void NavigateToCatalog(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnCatalogTab);
            var catalogTab = new CatalogTab();
            catalogTab.LogMessage += AddLog;
            MainFrame.Navigate(catalogTab);
        }
        
        private void NavigateToSystem(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnSystemTab);
            var systemTab = new SystemTab();
            systemTab.LogMessage += AddLog;
            MainFrame.Navigate(systemTab);
        }
        
        private void NavigateToOffice(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnOfficeTab);
            var officeTab = new OfficeTab();
            officeTab.LogMessage += AddLog;
            MainFrame.Navigate(officeTab);
        }
        
        private void NavigateToActivation(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnActivationTab);
            var activationTab = new ActivationTab();
            activationTab.LogMessage += AddLog;
            MainFrame.Navigate(activationTab);
        }
        
        private void NavigateToAbout(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnAboutTab);
            var aboutTab = new AboutTab();
            aboutTab.LogMessage += AddLog;
            MainFrame.Navigate(aboutTab);
        }
        
private void SetActiveButton(Button activeButton)
{
    var buttons = new[] { btnCatalogTab, btnSystemTab, btnOfficeTab, btnActivationTab, btnAboutTab, btnNetworkTab };
    foreach (var btn in buttons)
    {
        if (btn != null) btn.Style = (Style)FindResource("NavButtonStyle");
    }
    activeButton.Style = (Style)FindResource("ActiveNavButtonStyle");
}
        private void ToggleTheme(object sender, RoutedEventArgs e)
        {
            bool isDark = btnThemeToggle.IsChecked == false;
            
            var resources = Application.Current.Resources;
            
            if (isDark)
            {
                // Тёмная тема
                resources["WindowBackground"] = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                resources["SidebarBackground"] = new SolidColorBrush(Color.FromRgb(45, 45, 45));
                resources["ContentBackground"] = new SolidColorBrush(Color.FromRgb(37, 37, 38));
                resources["CardBackground"] = new SolidColorBrush(Color.FromRgb(45, 45, 45));
                resources["TextPrimary"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                resources["TextSecondary"] = new SolidColorBrush(Color.FromRgb(204, 204, 204));
                resources["BorderBrush"] = new SolidColorBrush(Color.FromRgb(61, 61, 61));
                resources["AccentColor"] = new SolidColorBrush(Color.FromRgb(0, 120, 212));
                resources["HeaderForeground"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                txtStatusBar.Text = "🌙 Тёмная тема";
            }
            else
            {
                // Светлая тема
                resources["WindowBackground"] = new SolidColorBrush(Color.FromRgb(240, 240, 240));
                resources["SidebarBackground"] = new SolidColorBrush(Color.FromRgb(248, 248, 248));
                resources["ContentBackground"] = new SolidColorBrush(Color.FromRgb(245, 245, 245));
                resources["CardBackground"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                resources["TextPrimary"] = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                resources["TextSecondary"] = new SolidColorBrush(Color.FromRgb(100, 100, 100));
                resources["BorderBrush"] = new SolidColorBrush(Color.FromRgb(220, 220, 220));
                resources["AccentColor"] = new SolidColorBrush(Color.FromRgb(0, 120, 212));
                resources["HeaderForeground"] = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                txtStatusBar.Text = "☀️ Светлая тема";
            }
        }
        
        private bool IsRunAsAdmin()
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        
        private void RestartAsAdmin()
        {
            var exeName = Process.GetCurrentProcess().MainModule?.FileName;
            if (exeName != null)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exeName,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                try { Process.Start(psi); } catch { }
            }
            Application.Current.Shutdown();
        }
        
        public void AddLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                txtStatusBar.Text = message.Length > 50 ? message.Substring(0, 47) + "..." : message;
            });
        }
    }
}
