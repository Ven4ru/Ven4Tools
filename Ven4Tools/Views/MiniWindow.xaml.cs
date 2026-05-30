using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using Ven4Tools.Models;
using Ven4Tools.Services;
using CatalogApp = Ven4Tools.Models.App;

namespace Ven4Tools.Views
{
    public partial class MiniWindow : Window
    {
        private CancellationTokenSource? _installCts;
        private readonly InstallationService _installer = new();
        private List<CatalogApp> _allApps = new();

        public event Action? OpenFullRequested;

        public MiniWindow()
        {
            InitializeComponent();

            // Position bottom-right of working area
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 20;
            Top  = area.Bottom - Height - 20;

            Loaded += (_, _) =>
            {
                _allApps = CatalogLoaderService.LoadedCatalog?.Apps ?? new List<CatalogApp>();
                txtMiniSearch.Focus();
                ApplyPlaceholder();
            };

            txtMiniSearch.GotFocus  += (_, _) => { if (txtMiniSearch.Text == (string)txtMiniSearch.Tag) txtMiniSearch.Text = ""; };
            txtMiniSearch.LostFocus += (_, _) => { if (string.IsNullOrWhiteSpace(txtMiniSearch.Text)) ApplyPlaceholder(); };
        }

        private void ApplyPlaceholder() => txtMiniSearch.Text = (string)txtMiniSearch.Tag;

        private void TxtMiniSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            string q = txtMiniSearch.Text.Trim();
            if (q == (string)txtMiniSearch.Tag || q.Length < 2)
            {
                lstMiniResults.ItemsSource = null;
                btnMiniInstall.IsEnabled   = false;
                txtMiniStatus.Text = "Введите минимум 2 символа для поиска";
                return;
            }

            var results = _allApps
                .Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                         || a.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                         || a.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Take(12)
                .ToList();

            lstMiniResults.ItemsSource = results;
            txtMiniStatus.Text = results.Count == 0
                ? "Ничего не найдено"
                : $"Найдено: {results.Count} — выберите и нажмите Enter или дважды кликните";
            btnMiniInstall.IsEnabled = false;
        }

        private void TxtMiniSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && lstMiniResults.Items.Count > 0)
            {
                lstMiniResults.SelectedIndex = 0;
                lstMiniResults.Focus();
            }
            else if (e.Key == Key.Enter && lstMiniResults.SelectedItem != null)
            {
                BtnMiniInstall_Click(sender, new RoutedEventArgs());
            }
            else if (e.Key == Key.Escape)
            {
                Close();
            }
        }

        private void LstMiniResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lstMiniResults.SelectedItem is App)
                BtnMiniInstall_Click(sender, new RoutedEventArgs());
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
        }

        private async void BtnMiniInstall_Click(object sender, RoutedEventArgs e)
        {
            if (lstMiniResults.SelectedItem is not CatalogApp catalogApp) return;

            var appInfo = new AppInfo
            {
                Id           = catalogApp.Id,
                DisplayName  = catalogApp.Name,
                Category     = AppCategory.Другое,
                AlternativeId = catalogApp.WingetId,
                InstallerUrls = !string.IsNullOrEmpty(catalogApp.DownloadUrl)
                    ? new System.Collections.Generic.List<string> { catalogApp.DownloadUrl }
                    : new(),
                ChocoId = catalogApp.ChocoId,
                ScoopId = catalogApp.ScoopId
            };

            btnMiniInstall.IsEnabled = false;
            btnMiniOpenFull.IsEnabled = false;
            progressMini.Visibility = Visibility.Visible;
            progressMini.Value = 0;
            txtMiniStatus.Text = $"⚙️ Устанавливаю {catalogApp.Name}...";

            _installCts = new CancellationTokenSource();
            var progress = new Progress<AppInstallProgress>(p =>
            {
                progressMini.Value = p.Percentage;
                txtMiniStatus.Text = p.Status;
            });

            try
            {
                var result = await _installer.InstallAppAsync(
                    appInfo, new[] { "winget", "msstore" },
                    _installCts.Token, progress, "C:\\");

                txtMiniStatus.Text = result.Success
                    ? $"✅ {catalogApp.Name} установлен!"
                    : $"❌ Не удалось установить: {result.Message}";
            }
            catch (OperationCanceledException)
            {
                txtMiniStatus.Text = "⏹ Отменено";
            }
            catch (Exception ex)
            {
                txtMiniStatus.Text = $"❌ {ex.Message}";
            }
            finally
            {
                progressMini.Value = 0;
                progressMini.Visibility = Visibility.Collapsed;
                btnMiniInstall.IsEnabled  = true;
                btnMiniOpenFull.IsEnabled = true;
                _installCts?.Dispose();
                _installCts = null;
            }
        }

        private void BtnMiniOpenFull_Click(object sender, RoutedEventArgs e)
        {
            OpenFullRequested?.Invoke();
            Close();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _installCts?.Cancel();
            _installer.Dispose();
        }

        private void LstMiniResults_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            btnMiniInstall.IsEnabled = lstMiniResults.SelectedItem is CatalogApp;
        }
    }
}
