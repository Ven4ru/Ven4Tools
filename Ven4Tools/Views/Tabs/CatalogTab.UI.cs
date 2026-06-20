using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class CatalogTab
    {
        private void ShowLoading() =>
            Dispatcher.Invoke(() => txtLoadingStatus.Visibility = Visibility.Visible);

        private void HideLoading() =>
            Dispatcher.Invoke(() => txtLoadingStatus.Visibility = Visibility.Collapsed);

        private void ShowCatalogError(string detail) => Dispatcher.Invoke(() =>
        {
            txtCatalogErrorDetail.Text = detail;
            pnlCatalogError.Visibility = Visibility.Visible;
        });

        private void HideCatalogError() =>
            Dispatcher.Invoke(() => pnlCatalogError.Visibility = Visibility.Collapsed);

        // ── Favorites ─────────────────────────────────────────────────────────

        private Button MakeStarButton(string appId)
        {
            bool fav = _favoritesService.IsFavorite(appId);
            var btn = new Button
            {
                Content = fav ? "★" : "☆",
                Width   = 20,
                Height  = 20,
                Margin  = new Thickness(4, 0, 0, 0),
                Tag     = appId,
                Background       = System.Windows.Media.Brushes.Transparent,
                BorderThickness  = new Thickness(0),
                Foreground       = fav
                    ? new SolidColorBrush(Color.FromRgb(255, 215, 0))
                    : new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                FontSize         = 14,
                Padding          = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip          = fav ? "Убрать из избранного" : "Добавить в избранное",
                Cursor           = Cursors.Hand
            };
            btn.Click += StarButton_Click;
            return btn;
        }

        private void StarButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string appId) return;
            _favoritesService.Toggle(appId);
            bool fav    = _favoritesService.IsFavorite(appId);
            btn.Content = fav ? "★" : "☆";
            btn.Foreground = fav
                ? new SolidColorBrush(Color.FromRgb(255, 215, 0))
                : new SolidColorBrush(Color.FromRgb(100, 100, 100));
            btn.ToolTip = fav ? "Убрать из избранного" : "Добавить в избранное";
        }

        // ── Version selection combo ───────────────────────────────────────────

        private ComboBox MakeVersionCombo(string appId)
        {
            var combo = new ComboBox
            {
                Width   = 95,
                Height  = 22,
                Margin  = new Thickness(4, 0, 0, 0),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = false,
                ToolTip   = "Версии загружаются...",
                Tag       = appId
            };
            combo.Items.Add("Последняя");
            combo.SelectedIndex = 0;
            return combo;
        }

        private async Task FetchVersionsForAppAsync(string appId, string wingetId)
        {
            var versions = await WingetVersionsService.FetchVersionsAsync(wingetId);
            if (versions.Count == 0) return;
            await Dispatcher.InvokeAsync(() =>
            {
                // Запись в _appVersions выполняется на UI-потоке: Phase2 запускает
                // несколько FetchVersionsForAppAsync параллельно, а Dictionary не
                // потокобезопасен — одновременная запись могла повредить структуру.
                _appVersions[appId] = versions;
                if (_versionCombos.TryGetValue(appId, out var combo))
                {
                    combo.Items.Clear();
                    combo.Items.Add("Последняя");
                    foreach (var v in versions)
                        combo.Items.Add(v);
                    combo.SelectedIndex = 0;
                    combo.IsEnabled     = true;
                    combo.ToolTip       = $"Доступных версий: {versions.Count}";
                }
            });
        }

        private async Task FetchVersionsPhase2Async()
        {
            if (_catalog == null) return;
            AddLog("🔍 Загрузка доступных версий (Фаза 2)...");

            var sem   = new SemaphoreSlim(3);
            var tasks = new List<Task>();

            foreach (var app in _catalog.Apps)
            {
                if (string.IsNullOrEmpty(app.WingetId)) continue;
                if (!_versionCombos.ContainsKey(app.Id)) continue;
                if (availabilityStatus.TryGetValue(app.Id, out var s) &&
                    s == AvailabilityChecker.AvailabilityStatus.Unavailable) continue;

                var localApp = app;
                tasks.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync();
                    try { await FetchVersionsForAppAsync(localApp.Id, localApp.WingetId); }
                    finally { sem.Release(); }
                }));
            }

            await Task.WhenAll(tasks);
            int loaded = _appVersions.Count;
            AddLog($"✅ Версии загружены для {loaded} приложений");
            _ = UpdateInstalledStatusAsync();
        }

        // ── Log ───────────────────────────────────────────────────────────────

        public void AddLog(string message) => AppLogger.Write(message);
    }
}
