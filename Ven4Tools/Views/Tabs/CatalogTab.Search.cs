using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class CatalogTab
    {
        private void ApplyProfileFilters()
        {
            var mode = ProfileService.Current.CatalogMode;
            bool compact = ProfileService.Current.CompactMode;
            int modeLevel = mode switch { "basic" => 0, "extended" => 1, _ => 2 };

            foreach (var kvp in categoryPanels)
            {
                int visibleCount = 0;
                foreach (FrameworkElement child in kvp.Value.Children)
                {
                    string? appId = GetChildAppId(child);
                    bool profileOk = true;
                    if (appId != null && _catalog != null)
                    {
                        var app = _catalog.Apps.FirstOrDefault(a => a.Id == appId);
                        if (app != null)
                        {
                            int appLevel = app.Profile switch { "extended" => 1, "full" => 2, _ => 0 };
                            profileOk = appLevel <= modeLevel;
                        }
                    }
                    child.Visibility = profileOk ? Visibility.Visible : Visibility.Collapsed;
                    child.Margin = compact ? new Thickness(0) : new Thickness(0, 2, 0, 2);
                    if (profileOk) visibleCount++;
                }

                if (kvp.Value.Parent is Expander expander)
                    expander.Visibility = visibleCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ProfileService.Current.HideInstalled)
                ApplyHideInstalled();
        }

        private void OnAppSettingsChanged()
        {
            _catalogLoader?.UpdateTimeout(AppSettings.CatalogTimeout);
            availabilityChecker.UpdateTimeout(AppSettings.CheckTimeout);
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string text         = txtSearch.Text;
            bool isPlaceholder  = text == (string)txtSearch.Tag;
            string query        = isPlaceholder ? "" : text;

            btnClearSearch.Visibility = query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            int visible = FilterApps(query);

            _searchDebounce?.Cancel();

            if (query.Length >= 2 && visible == 0)
            {
                pnlWingetSuggestions.Visibility = Visibility.Visible;
                pnlWingetResults.Children.Clear();
                txtWingetStatus.Text       = "⏳ Поиск по источникам...";
                txtWingetStatus.Visibility = Visibility.Visible;

                _searchDebounce = new CancellationTokenSource();
                var token = _searchDebounce.Token;
                _ = Task.Delay(600, token).ContinueWith(async t =>
                {
                    if (t.IsCanceled) return;
                    var wingetTask = WingetService.SearchAsync(query, token);
                    var chocoTask  = PackageManagerService.SearchChocoAsync(query, token);
                    var scoopTask  = PackageManagerService.SearchScoopAsync(query, token);
                    await Task.WhenAll(wingetTask, chocoTask, scoopTask);
                    if (!token.IsCancellationRequested)
                        await Dispatcher.InvokeAsync(() =>
                            ShowAllSuggestions(query, wingetTask.Result, chocoTask.Result, scoopTask.Result));
                });
            }
            else
            {
                HideWingetSuggestions();
            }
        }

        private void BtnFavoritesOnly_Click(object sender, RoutedEventArgs e)
        {
            _showFavoritesOnly = !_showFavoritesOnly;
            btnFavoritesOnly.Foreground = _showFavoritesOnly
                ? new SolidColorBrush(Color.FromRgb(255, 215, 0))
                : new SolidColorBrush(Color.FromRgb(100, 100, 100));
            btnFavoritesOnly.ToolTip = _showFavoritesOnly ? "Показать все" : "Только избранные";

            string query = txtSearch.Text == (string)txtSearch.Tag ? "" : txtSearch.Text;
            FilterApps(query);
            HideWingetSuggestions();
        }

        private int FilterApps(string searchText)
        {
            bool hasSearch = !string.IsNullOrWhiteSpace(searchText);
            bool filtered  = hasSearch || _showFavoritesOnly;
            string lower   = searchText.ToLower();

            if (!filtered)
            {
                foreach (var kvp in categoryPanels)
                    foreach (FrameworkElement child in kvp.Value.Children)
                        child.Visibility = Visibility.Visible;
                ApplyProfileFilters();
                return -1;
            }

            int totalVisible = 0;

            foreach (var kvp in categoryPanels)
            {
                int panelVisible = 0;
                foreach (FrameworkElement child in kvp.Value.Children)
                {
                    string? label = GetChildLabel(child);
                    string? appId = GetChildAppId(child);

                    bool matchSearch = !hasSearch || (label?.ToLower().Contains(lower) == true);
                    bool matchFav    = !_showFavoritesOnly || (appId != null && _favoritesService.IsFavorite(appId));

                    bool visible = matchSearch && matchFav;
                    child.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                    if (visible) { panelVisible++; totalVisible++; }
                }

                if (kvp.Value.Parent is System.Windows.Controls.Expander expander)
                {
                    expander.Visibility = panelVisible > 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (panelVisible > 0) expander.IsExpanded = true;
                }
            }

            return totalVisible;
        }

        private static string? GetChildLabel(FrameworkElement child)
        {
            if (child is CheckBox cb && cb.Content is TextBlock tb)
                return tb.Text;
            if (child is StackPanel sp)
            {
                var innerCb = sp.Children.OfType<CheckBox>().FirstOrDefault();
                if (innerCb?.Content is TextBlock innerTb)
                    return innerTb.Text;
            }
            return null;
        }

        private static string? GetChildAppId(FrameworkElement child)
        {
            if (child is CheckBox cb)
                return cb.Tag?.ToString();
            if (child is StackPanel sp)
            {
                var innerCb = sp.Children.OfType<CheckBox>().FirstOrDefault();
                return innerCb?.Tag?.ToString();
            }
            return null;
        }

        private void ShowAllSuggestions(
            string query,
            List<WingetPackage> winget,
            List<(string Id, string Name, string Version)> choco,
            List<(string Id, string Name)> scoop)
        {
            pnlWingetResults.Children.Clear();
            txtWingetStatus.Visibility = Visibility.Collapsed;

            bool any = winget.Count > 0 || choco.Count > 0 || scoop.Count > 0;
            if (!any)
            {
                txtWingetStatus.Text       = $"😕 Ничего не найдено по запросу «{query}» ни в одном источнике";
                txtWingetStatus.Visibility = Visibility.Visible;
                return;
            }

            if (winget.Count > 0)
            {
                AddSectionHeader("📦 Winget");
                foreach (var pkg in winget)
                {
                    var captureId = pkg.Id; var captureName = pkg.Name;
                    AddSuggestionRow(pkg.Name, $"winget:{pkg.Id}", pkg.Id,
                        () => AddWingetSuggestion(captureName, captureId));
                }
            }

            if (choco.Count > 0)
            {
                AddSectionHeader("🍫 Chocolatey");
                foreach (var (id, name, ver) in choco)
                {
                    var captureId = id;
                    AddSuggestionRow(name.Length > 0 ? name : id, $"v{ver}", id,
                        () => AddChocoSuggestion(captureId));
                }
            }

            if (scoop.Count > 0)
            {
                AddSectionHeader("🪣 Scoop");
                foreach (var (id, name) in scoop)
                {
                    var captureId = id;
                    AddSuggestionRow(id, "", id,
                        () => AddScoopSuggestion(captureId));
                }
            }
        }

        private void AddSectionHeader(string title)
        {
            var border = new Border
            {
                Margin          = new Thickness(0, 6, 0, 2),
                BorderBrush     = (System.Windows.Media.Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding         = new Thickness(0, 0, 0, 2)
            };
            border.Child = new TextBlock
            {
                Text       = title,
                FontSize   = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary")
            };
            pnlWingetResults.Children.Add(border);
        }

        private void AddSuggestionRow(string name, string hint, string tooltip, Action onClick)
        {
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 1, 0, 1), Height = 26,
                Padding = new Thickness(6, 0, 6, 0),
                Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand, FontSize = 12, ToolTip = tooltip
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = "➕  ", Foreground = (System.Windows.Media.Brush)FindResource("AccentColor"), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = name, Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"), VerticalAlignment = VerticalAlignment.Center });
            if (!string.IsNullOrEmpty(hint))
                row.Children.Add(new TextBlock { Text = $"  {hint}", Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
            btn.Content = row;
            btn.Click  += (_, _) => onClick();
            pnlWingetResults.Children.Add(btn);
        }

        private async void AddChocoSuggestion(string id)
        {
            try
            {
                // Префикс User. отделяет пользовательский Id от каталожного —
                // иначе совпадение Id перезаписывало бы чекбокс из каталога.
                string userId = $"User.{id}";
                if (appManager.GetAppById(userId) != null) { AddLog($"ℹ️ {id} уже есть в списке"); return; }
                var app = new AppInfo { Id = userId, DisplayName = id, Category = AppCategory.Другое, ChocoId = id, InstallerUrls = new List<string>(), IsUserAdded = true };
                appManager.AddUserApp(app);
                AddUserAppToUI(app);

                if (UserSession.IsLoggedIn)
                    await _userAppsService.SaveAsync(app);

                AddLog($"➕ Добавлено из Chocolatey: {id}");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка добавления приложения: {ex.Message}");
            }
        }

        private async void AddScoopSuggestion(string id)
        {
            try
            {
                // Префикс User. отделяет пользовательский Id от каталожного —
                // иначе совпадение Id перезаписывало бы чекбокс из каталога.
                string userId = $"User.{id}";
                if (appManager.GetAppById(userId) != null) { AddLog($"ℹ️ {id} уже есть в списке"); return; }
                var app = new AppInfo { Id = userId, DisplayName = id, Category = AppCategory.Другое, ScoopId = id, InstallerUrls = new List<string>(), IsUserAdded = true };
                appManager.AddUserApp(app);
                AddUserAppToUI(app);

                if (UserSession.IsLoggedIn)
                    await _userAppsService.SaveAsync(app);

                AddLog($"➕ Добавлено из Scoop: {id}");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка добавления приложения: {ex.Message}");
            }
        }

        private void HideWingetSuggestions()
        {
            Dispatcher.Invoke(() =>
            {
                pnlWingetSuggestions.Visibility = Visibility.Collapsed;
                pnlWingetResults.Children.Clear();
            });
        }

        private async void AddWingetSuggestion(string name, string id)
        {
            try
            {
                if (!UserSession.IsLoggedIn)
                {
                    var prompt = MessageBox.Show(
                        $"Добавить «{name}» в локальный список?\n\nДля синхронизации между устройствами войдите в аккаунт.",
                        "Добавить приложение", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (prompt != MessageBoxResult.Yes) return;
                }

                if (appManager.GetAppById(id) != null)
                {
                    AddLog($"ℹ️ {name} уже есть в списке");
                    return;
                }

                var newApp = new AppInfo
                {
                    Id = id,
                    DisplayName = name,
                    Category = AppCategory.Другое,
                    AlternativeId = id,
                    InstallerUrls = new List<string>(),
                    SilentArgs = "",
                    IsUserAdded = true
                };

                appManager.AddUserApp(newApp);
                AddUserAppToUI(newApp);

                if (UserSession.IsLoggedIn)
                    await _userAppsService.SaveAsync(newApp);

                AddLog($"➕ Добавлено из winget: {name} ({id})");

                txtSearch.Text = (string)txtSearch.Tag;
                HideWingetSuggestions();
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка добавления приложения: {ex.Message}");
            }
        }
    }
}
