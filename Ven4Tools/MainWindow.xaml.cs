using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
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

        private readonly ObservableCollection<LogEntry> _logEntries = new();

        private CatalogTab?    _catalogTab;
        private InstalledTab?  _installedTab;
        private SystemTab?     _systemTab;
        private OfficeTab?     _officeTab;
        private ActivationTab? _activationTab;
        private AboutTab?      _aboutTab;
        private NetworkTab?    _networkTab;
        private HistoryTab?    _historyTab;
        private DebloaterTab?  _debloaterTab;
        private System.Windows.Forms.NotifyIcon? _trayIcon;

        public MainWindow()
        {
            InitializeComponent();
            lstGlobalLog.ItemsSource = _logEntries;
            AppLogger.MessageReceived += AddLog;

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
            Loaded += (s, e) =>
            {
                ConnectivityMonitor.Start();
                ConnectivityMonitor.StatusChanged += online => Dispatcher.Invoke(() => UpdateTabVisibility());
                UpdateTabVisibility();
            };
        }

        private void NavigateToCatalog(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnCatalogTab);
            if (sender != null) AppLogger.Write("📂 Открыта вкладка: Каталог");
            if (_catalogTab == null)
            {
                _catalogTab = new CatalogTab();
                _catalogTab.SwitchToUpdatesRequested += () =>
                {
                    if (_installedTab == null) _installedTab = new InstalledTab();
                    _installedTab.ShowUpdatesFilter();
                    NavigateToInstalled(null, null);
                };
            }
            MainFrame.Content = (_catalogTab);
            UpdateMascot("catalog");
        }

        private void NavigateToNetwork(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnNetworkTab);
            AppLogger.Write("📂 Открыта вкладка: Сеть");
            if (_networkTab == null) _networkTab = new NetworkTab();
            MainFrame.Content = (_networkTab);
            UpdateMascot("network");
        }

        private void NavigateToInstalled(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnInstalledTab);
            if (sender != null) AppLogger.Write("📂 Открыта вкладка: Установленные");
            if (_installedTab == null) _installedTab = new InstalledTab();
            MainFrame.Content = (_installedTab);
            UpdateMascot("installed");
        }

        private void NavigateToSystem(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnSystemTab);
            AppLogger.Write("📂 Открыта вкладка: Система");
            if (_systemTab == null) _systemTab = new SystemTab();
            MainFrame.Content = (_systemTab);
            UpdateMascot("system");
        }

        private void NavigateToOffice(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnOfficeTab);
            AppLogger.Write("📂 Открыта вкладка: Office");
            if (_officeTab == null)
            {
                _officeTab = new OfficeTab();
                _officeTab.GoToActivation += () => NavigateToActivation(null, null);
            }
            MainFrame.Content = (_officeTab);
            UpdateMascot("office");
        }

        private void NavigateToActivation(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnActivationTab);
            if (sender != null) AppLogger.Write("📂 Открыта вкладка: Активация");
            if (_activationTab == null) _activationTab = new ActivationTab();
            MainFrame.Content = (_activationTab);
            UpdateMascot("activation");
        }

        private void NavigateToAbout(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnAboutTab);
            AppLogger.Write("📂 Открыта вкладка: О программе");
            if (_aboutTab == null) _aboutTab = new AboutTab();
            MainFrame.Content = (_aboutTab);
            UpdateMascot("about");
        }

        public void UpdateTabVisibility()
        {
            bool online    = ConnectivityMonitor.IsOnline && !ProfileService.Current.OfflineMode;
            bool loggedIn  = UserSession.IsLoggedIn;

            // Online-only tabs
            btnOfficeTab.Visibility     = online ? Visibility.Visible : Visibility.Collapsed;
            btnActivationTab.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
            btnNetworkTab.Visibility    = online ? Visibility.Visible : Visibility.Collapsed;

            // Auth-gated tabs
            btnHistoryTab.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;

            if (!online)
            {
                string reason = !ConnectivityMonitor.IsOnline
                    ? "🔌 Нет интернета — часть вкладок скрыта"
                    : "🔌 Офлайн режим — часть вкладок скрыта";
                txtStatusBar.Text = reason;

                if (_currentTab is "office" or "activation" or "network")
                    NavigateToCatalog(null, null);
            }
            else
            {
                txtStatusBar.Text = "✅ Готово";
            }

            // If auth-gated tab is active but user logged out — go to catalog
            if (!loggedIn && _currentTab is "history")
                NavigateToCatalog(null, null);
        }

        // ── Debloater tab ─────────────────────────────────────────────────────────

        private void NavigateToDebloater(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnDebloaterTab);
            AppLogger.Write("📂 Открыта вкладка: Очистка");
            if (_debloaterTab == null) _debloaterTab = new DebloaterTab();
            MainFrame.Content = (_debloaterTab);
            UpdateMascot("debloater");
        }

        // ── History tab ───────────────────────────────────────────────────────────

        private void NavigateToHistory(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnHistoryTab);
            AppLogger.Write("📂 Открыта вкладка: История");
            if (_historyTab == null) _historyTab = new HistoryTab();
            MainFrame.Content = (_historyTab);
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
            ConnectivityMonitor.Stop();
            Application.Current.Shutdown();
        }

        private void ChkMinimizeToTray_Click(object sender, RoutedEventArgs e)
        {
            ProfileService.Current.MinimizeToTray = chkMinimizeToTray.IsChecked == true;
            ProfileService.Save();
        }

        private void Window_Closing_Extended(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Предупреждение при закрытии во время активной установки.
            // При сворачивании в трей окно не закрывается — установка продолжается, предупреждение не нужно.
            if (!ProfileService.Current.MinimizeToTray && _catalogTab?.IsInstalling == true)
            {
                var res = MessageBox.Show(
                    "Идёт установка приложений.\n\nЗакрыть программу и прервать установку?",
                    "Установка в процессе",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);

                // OK — закрыть, Отмена — продолжить работу
                if (res != MessageBoxResult.OK)
                {
                    e.Cancel = true;
                    return;
                }
            }

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
                ConnectivityMonitor.Stop();
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
            if (catalogApp == null) { AppLogger.Write($"❌ Приложение {id} не найдено в каталоге"); return; }

            AppLogger.Write($"📌 Установка из пина: {catalogApp.Name}...");
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
            var prog = new Progress<Services.AppInstallProgress>(p => AppLogger.Write($"  {p.Status}"));
            async Task<bool> confirmPm(string pmName) =>
                await Dispatcher.InvokeAsync(() =>
                    System.Windows.MessageBox.Show(
                        $"Для установки требуется {pmName}, который не установлен.\n\nРазрешить установку {pmName}?",
                        $"Установка {pmName}",
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes);

            var r = await installer.InstallAppAsync(appInfo, new[] { "winget", "msstore" }, cts.Token, prog, "C:\\", null, confirmPm);
            AppLogger.Write(r.Success ? $"✅ {catalogApp.Name}" : $"❌ {r.Message}");
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

        // ── Drag & drop ───────────────────────────────────────────────────────────

        private void MainArea_DragEnter(object sender, DragEventArgs e)
        {
            if (IsExeOrMsi(e))
            {
                e.Effects = DragDropEffects.Copy;
                pnlDropOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void MainArea_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = IsExeOrMsi(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void MainArea_DragLeave(object sender, DragEventArgs e)
        {
            pnlDropOverlay.Visibility = Visibility.Collapsed;
        }

        private void MainArea_Drop(object sender, DragEventArgs e)
        {
            pnlDropOverlay.Visibility = Visibility.Collapsed;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (var file in files)
            {
                if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && !file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) continue;

                var dlg = new Views.LocalInstallerDialog(file) { Owner = this };
                if (dlg.ShowDialog() == true && dlg.Result != null)
                {
                    AppLogger.Write($"📦 Добавлен локальный установщик: {dlg.Result.DisplayName}");
                    // Pass to CatalogTab's user apps mechanism
                    if (_catalogTab != null)
                        _catalogTab.AddLocalInstallerApp(dlg.Result);
                }
            }
        }

        private static bool IsExeOrMsi(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            return files.Any(f =>
                f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
        }

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
            var buttons = new[] { btnCatalogTab, btnInstalledTab, btnSystemTab, btnOfficeTab, btnActivationTab, btnAboutTab, btnNetworkTab, btnHistoryTab, btnDebloaterTab };
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
                var entry = LogEntry.Parse(message);
                _logEntries.Add(entry);
                lstGlobalLog.ScrollIntoView(entry);
                txtStatusBar.Text = message.Length > 50 ? message.Substring(0, 47) + "..." : message;
            });
        }

        private void BtnClearGlobalLog_Click(object sender, RoutedEventArgs e) => _logEntries.Clear();

        private void CopyGlobalLog_Click(object sender, RoutedEventArgs e)
        {
            var items = lstGlobalLog.SelectedItems.Count > 0
                ? lstGlobalLog.SelectedItems.Cast<LogEntry>()
                : _logEntries.AsEnumerable();
            var text = string.Join(Environment.NewLine,
                items.Select(entry => $"[{entry.Time}] {entry.Icon} {entry.Message}"));
            if (!string.IsNullOrEmpty(text))
                Clipboard.SetText(text);
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
