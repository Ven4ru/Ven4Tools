using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.Views;
using Ven4Tools.Views.Tabs;

namespace Ven4Tools
{
    public partial class MainWindow : Window
    {
        private bool _categorySelectionShown = false;

        public MainWindow()
        {
            InitializeComponent();

            if (!IsRunAsAdmin())
            {
                RestartAsAdmin();
                return;
            }

            NavigateToCatalog(null, null);

            // Sync theme toggle with saved profile
            btnThemeToggle.IsChecked = ProfileService.Current.Theme == "light";

            UserSession.Changed += UpdateUserUI;
            UpdateUserUI();

            Loaded += (s, e) => ShowCategorySelectionIfNeeded();
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
            ThemeService.ApplyDark(isDark);
            ProfileService.Current.Theme = isDark ? "dark" : "light";
            ProfileService.Save();
            txtStatusBar.Text = isDark ? "🌙 Тёмная тема" : "☀️ Светлая тема";
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

        private void UpdateUserUI()
        {
            Dispatcher.Invoke(() =>
            {
                if (UserSession.IsLoggedIn)
                {
                    txtUserName.Text = UserSession.Name;
                    txtUserEmail.Text = UserSession.Email;
                    pnlUserLoggedIn.Visibility = Visibility.Visible;
                    btnLogin.Visibility = Visibility.Collapsed;
                    txtStatusBar.Text = $"👋 Привет, {UserSession.Name}!";
                    ShowCategorySelectionIfNeeded();
                }
                else
                {
                    pnlUserLoggedIn.Visibility = Visibility.Collapsed;
                    btnLogin.Visibility = Visibility.Visible;
                }
            });
        }

        private void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            var win = new LoginWindow { Owner = this };
            win.ShowDialog();
        }

        private void BtnLogout_Click(object sender, RoutedEventArgs e)
        {
            UserSession.Logout();
            txtStatusBar.Text = "✅ Вы вышли из аккаунта";
        }

        private void BtnProfile_Click(object sender, RoutedEventArgs e)
        {
            var win = new ProfileWindow { Owner = this };
            win.ShowDialog();
            // Sync theme toggle in case profile changed theme
            btnThemeToggle.IsChecked = ProfileService.Current.Theme == "light";
        }

        private void ShowCategorySelectionIfNeeded()
        {
            if (_categorySelectionShown) return;
            if (!UserSession.IsLoggedIn || ProfileService.Current.HasSelectedCategory) return;
            _categorySelectionShown = true;

            var win = new CategorySelectionWindow { Owner = this };
            if (win.ShowDialog() != true)
                _categorySelectionShown = false;
        }
    }
}
