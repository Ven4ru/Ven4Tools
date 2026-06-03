using System;
using System.Linq;
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
        private string _currentTab = "catalog";
        private bool _feedbackShown = false;

        private CatalogTab?    _catalogTab;
        private InstalledTab?  _installedTab;
        private SystemTab?     _systemTab;
        private OfficeTab?     _officeTab;
        private ActivationTab? _activationTab;
        private AboutTab?      _aboutTab;
        private NetworkTab?    _networkTab;
        private HistoryTab?    _historyTab;
        private System.Windows.Forms.NotifyIcon? _trayIcon;

        public MainWindow()
        {
            InitializeComponent();

            if (!IsRunAsAdmin())
            {
                RestartAsAdmin();
                return;
            }

            NavigateToCatalog(null, null);

            UserSession.Changed += UpdateUserUI;
            Closing += (_, _) => UserSession.Changed -= UpdateUserUI;
            Closing += (_, args) =>
            {
                if (ChannelService.IsPreRelease && !_feedbackShown)
                {
                    args.Cancel = true;
                    _feedbackShown = true;
                    var fw = new Views.FeedbackWindow { Owner = this };
                    fw.Closed += (_, _) => Close();
                    fw.Show();
                }
            };
            UpdateUserUI();

            Loaded += (s, e) => ShowCategorySelectionIfNeeded();
            Loaded += (s, e) =>
            {
                InitTrayIcon();
                chkMinimizeToTray.IsChecked = ProfileService.Current.MinimizeToTray;
                RefreshPinsStrip();
            };
            Loaded += (s, e) => UpdateTabVisibility();
        }

        private void NavigateToCatalog(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnCatalogTab);
            if (_catalogTab == null)
            {
                _catalogTab = new CatalogTab();
                _catalogTab.LogMessage += AddLog;
                _catalogTab.SwitchToUpdatesRequested += () =>
                {
                    if (_installedTab == null) { _installedTab = new InstalledTab(); _installedTab.LogMessage += AddLog; }
                    _installedTab.ShowUpdatesFilter();
                    NavigateToInstalled(null, null);
                };
            }
            MainFrame.Navigate(_catalogTab);
            UpdateMascot("catalog");
        }

        private void NavigateToNetwork(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnNetworkTab);
            if (_networkTab == null) { _networkTab = new NetworkTab(); _networkTab.LogMessage += AddLog; }
            MainFrame.Navigate(_networkTab);
            UpdateMascot("network");
        }

        private void NavigateToInstalled(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnInstalledTab);
            if (_installedTab == null) { _installedTab = new InstalledTab(); _installedTab.LogMessage += AddLog; }
            MainFrame.Navigate(_installedTab);
            UpdateMascot("installed");
        }

        private void NavigateToSystem(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnSystemTab);
            if (_systemTab == null) { _systemTab = new SystemTab(); _systemTab.LogMessage += AddLog; }
            MainFrame.Navigate(_systemTab);
            UpdateMascot("system");
        }

        private void NavigateToOffice(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnOfficeTab);
            if (_officeTab == null)
            {
                _officeTab = new OfficeTab();
                _officeTab.LogMessage += AddLog;
                _officeTab.GoToActivation += () => NavigateToActivation(null, null);
            }
            MainFrame.Navigate(_officeTab);
            UpdateMascot("office");
        }

        private void NavigateToActivation(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnActivationTab);
            if (_activationTab == null) { _activationTab = new ActivationTab(); _activationTab.LogMessage += AddLog; }
            MainFrame.Navigate(_activationTab);
            UpdateMascot("activation");
        }

        private void NavigateToAbout(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnAboutTab);
            if (_aboutTab == null) { _aboutTab = new AboutTab(); _aboutTab.LogMessage += AddLog; }
            MainFrame.Navigate(_aboutTab);
            UpdateMascot("about");
        }

        public void UpdateTabVisibility()
        {
            bool loggedIn = UserSession.IsLoggedIn;
            btnHistoryTab.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
            if (!loggedIn && _currentTab == "history")
                NavigateToCatalog(null, null);
        }

        // ── History tab ───────────────────────────────────────────────────────────

        private void NavigateToHistory(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnHistoryTab);
            if (_historyTab == null) { _historyTab = new HistoryTab(); _historyTab.LogMessage += AddLog; }
            MainFrame.Navigate(_historyTab);
            _ = _historyTab.RefreshAsync();
            UpdateMascot("history");
        }

        // ── Tray icon ─────────────────────────────────────────────────────────────

        private void InitTrayIcon()
        {
            try
            {
                _trayIcon = new System.Windows.Forms.NotifyIcon
                {
                    Icon    = System.Drawing.Icon.ExtractAssociatedIcon(
                                  System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                                  ?? ""),
                    Visible = true,
                    Text    = "Ven4Tools"
                };

                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("Открыть", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
                menu.Items.Add("-");
                menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(ForceExit));

                _trayIcon.ContextMenuStrip = menu;
                _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
            }
            catch { }
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void ForceExit()
        {
            _trayIcon?.Dispose();
            Application.Current.Shutdown();
        }

        private void ChkMinimizeToTray_Click(object sender, RoutedEventArgs e)
        {
            ProfileService.Current.MinimizeToTray = chkMinimizeToTray.IsChecked == true;
            ProfileService.Save();
        }

        private void Window_Closing_Extended(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (ProfileService.Current.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                _trayIcon?.ShowBalloonTip(2000, "Ven4Tools",
                    "Приложение свёрнуто в трей. Двойной клик для открытия.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
            else
            {
                _trayIcon?.Dispose();
            }
        }

        // ── Quick pins ────────────────────────────────────────────────────────────

        public void RefreshPinsStrip()
        {
            var pins = ProfileService.Current.PinnedAppIds;
            if (pins.Count == 0) { pnlPins.Visibility = Visibility.Collapsed; return; }

            pnlPins.Visibility = Visibility.Visible;
            wrapPins.Children.Clear();
            var catalog = Services.CatalogLoaderService.LoadedCatalog;

            foreach (var id in pins)
            {
                var app = catalog?.Apps.FirstOrDefault(a => a.Id == id);
                string name = app?.Name ?? id;

                var card = new Border
                {
                    Background    = (Brush)FindResource("CardBackground"),
                    CornerRadius  = new CornerRadius(8),
                    Padding       = new Thickness(8, 4, 4, 4),
                    Margin        = new Thickness(0, 0, 6, 0),
                    Cursor        = System.Windows.Input.Cursors.Hand
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock
                {
                    Text = name.Length > 16 ? name.Substring(0, 16) + "…" : name,
                    Foreground = (Brush)FindResource("TextPrimary"),
                    FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                });
                var installBtn = new Button
                {
                    Content = "▶", Width = 22, Height = 22, FontSize = 9,
                    Tag = id, Padding = new Thickness(0),
                    ToolTip = $"Установить {name}"
                };
                installBtn.Click += PinInstallBtn_Click;
                var unpinBtn = new Button
                {
                    Content = "×", Width = 18, Height = 18, FontSize = 10,
                    Tag = id, Padding = new Thickness(0),
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = (Brush)FindResource("TextSecondary"),
                    ToolTip = "Открепить"
                };
                unpinBtn.Click += PinUnpinBtn_Click;
                row.Children.Add(installBtn);
                row.Children.Add(unpinBtn);
                card.Child = row;
                wrapPins.Children.Add(card);
            }
        }

        private async void PinInstallBtn_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not string id) return;
            var catalog = Services.CatalogLoaderService.LoadedCatalog;
            var catalogApp = catalog?.Apps.FirstOrDefault(a => a.Id == id);
            if (catalogApp == null) { AddLog($"❌ Приложение {id} не найдено в каталоге"); return; }

            AddLog($"📌 Установка из пина: {catalogApp.Name}...");
            using var installer = new Services.InstallationService();
            var appInfo = new Models.AppInfo
            {
                Id = catalogApp.Id, DisplayName = catalogApp.Name,
                AlternativeId = catalogApp.WingetId,
                InstallerUrls = !string.IsNullOrEmpty(catalogApp.DownloadUrl)
                    ? new System.Collections.Generic.List<string> { catalogApp.DownloadUrl } : new(),
                ChocoId = catalogApp.ChocoId, ScoopId = catalogApp.ScoopId
            };
            var cts = new System.Threading.CancellationTokenSource();
            var prog = new Progress<Services.AppInstallProgress>(p => AddLog($"  {p.Status}"));
            var r = await installer.InstallAppAsync(appInfo, new[] { "winget", "msstore" }, cts.Token, prog, "C:\\");
            AddLog(r.Success ? $"✅ {catalogApp.Name}" : $"❌ {r.Message}");
        }

        private void PinUnpinBtn_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not string id) return;
            ProfileService.Current.PinnedAppIds.Remove(id);
            ProfileService.Save();
            RefreshPinsStrip();
        }

        public static void PinApp(string id)
        {
            var pins = ProfileService.Current.PinnedAppIds;
            if (pins.Contains(id) || pins.Count >= 6) return;
            pins.Add(id);
            ProfileService.Save();
        }

        public static void UnpinApp(string id)
        {
            ProfileService.Current.PinnedAppIds.Remove(id);
            ProfileService.Save();
        }

        public static bool IsPinned(string id) =>
            ProfileService.Current.PinnedAppIds.Contains(id);

        private void UpdateMascot(string tabName)
        {
            _currentTab = tabName;
            if (ProfileService.Current.Theme != "web")
            {
                imgMascot.Visibility = Visibility.Collapsed;
                return;
            }
            try
            {
                var uri = new Uri($"pack://application:,,,/Resources/Mascots/{tabName}.png");
                imgMascot.Source = new System.Windows.Media.Imaging.BitmapImage(uri);
                imgMascot.Visibility = Visibility.Visible;
            }
            catch
            {
                imgMascot.Visibility = Visibility.Collapsed;
            }
        }

        private void SetActiveButton(Button activeButton)
        {
            var buttons = new[] { btnCatalogTab, btnInstalledTab, btnSystemTab, btnOfficeTab, btnActivationTab, btnAboutTab, btnNetworkTab, btnHistoryTab };
            foreach (var btn in buttons)
            {
                if (btn != null) btn.Style = (Style)FindResource("NavButtonStyle");
            }
            activeButton.Style = (Style)FindResource("ActiveNavButtonStyle");
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
            if (exeName == null) return;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = exeName, UseShellExecute = true, Verb = "runas" });
                Application.Current.Shutdown();
            }
            catch { } // пользователь отклонил UAC — оставляем приложение работать
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
                UpdateTabVisibility();
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
            UpdateMascot(_currentTab);
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
