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

        private CatalogTab?    _catalogTab;
        private SystemTab?     _systemTab;
        private OfficeTab?     _officeTab;
        private ActivationTab? _activationTab;
        private AboutTab?      _aboutTab;
        private NetworkTab?    _networkTab;

        public MainWindow()
        {
            InitializeComponent();

            if (!IsRunAsAdmin())
            {
                RestartAsAdmin();
                return;
            }

            NavigateToCatalog(null, null);

            btnThemeToggle.IsChecked = ProfileService.Current.Theme == "light";

            UserSession.Changed += UpdateUserUI;
            Closing += (_, _) => UserSession.Changed -= UpdateUserUI;
            UpdateUserUI();

            Loaded += (s, e) => ShowCategorySelectionIfNeeded();
        }

        private void NavigateToCatalog(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnCatalogTab);
            if (_catalogTab == null)
            {
                _catalogTab = new CatalogTab();
                _catalogTab.LogMessage += AddLog;
            }
            MainFrame.Navigate(_catalogTab);
        }

        private void NavigateToNetwork(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnNetworkTab);
            if (_networkTab == null)
            {
                _networkTab = new NetworkTab();
                _networkTab.LogMessage += AddLog;
            }
            MainFrame.Navigate(_networkTab);
        }

        private void NavigateToSystem(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnSystemTab);
            if (_systemTab == null)
            {
                _systemTab = new SystemTab();
                _systemTab.LogMessage += AddLog;
            }
            MainFrame.Navigate(_systemTab);
        }

        private void NavigateToOffice(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnOfficeTab);
            if (_officeTab == null)
            {
                _officeTab = new OfficeTab();
                _officeTab.LogMessage += AddLog;
            }
            MainFrame.Navigate(_officeTab);
        }

        private void NavigateToActivation(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnActivationTab);
            if (_activationTab == null)
            {
                _activationTab = new ActivationTab();
                _activationTab.LogMessage += AddLog;
            }
            MainFrame.Navigate(_activationTab);
        }

        private void NavigateToAbout(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnAboutTab);
            if (_aboutTab == null)
            {
                _aboutTab = new AboutTab();
                _aboutTab.LogMessage += AddLog;
            }
            MainFrame.Navigate(_aboutTab);
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
