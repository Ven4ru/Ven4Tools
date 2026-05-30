using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class HistoryTab : UserControl
    {
        public event Action<string>? LogMessage;

        private List<HistoryEntry> _allEntries = new();

        public HistoryTab()
        {
            InitializeComponent();
            Loaded += async (_, _) => await RefreshAsync();
            InstallHistoryService.Instance.Changed += () => Dispatcher.Invoke(async () => await RefreshAsync());

            txtHistorySearch.GotFocus  += (_, _) => { if (txtHistorySearch.Text == (string)txtHistorySearch.Tag) txtHistorySearch.Text = ""; };
            txtHistorySearch.LostFocus += (_, _) => { if (string.IsNullOrWhiteSpace(txtHistorySearch.Text)) txtHistorySearch.Text = (string)txtHistorySearch.Tag; };
            txtHistorySearch.Text = (string)txtHistorySearch.Tag;
        }

        public async Task RefreshAsync()
        {
            _allEntries = await InstallHistoryService.Instance.GetHistoryAsync();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            string q = txtHistorySearch.Text.Trim();
            bool onlySuccess = togSuccessOnly.IsChecked == true;
            bool onlyFail    = togFailOnly.IsChecked == true;

            var filtered = _allEntries.AsEnumerable();

            if (!string.IsNullOrEmpty(q) && q != (string)txtHistorySearch.Tag)
                filtered = filtered.Where(e => e.AppName.Contains(q, StringComparison.OrdinalIgnoreCase)
                                            || e.Category.Contains(q, StringComparison.OrdinalIgnoreCase));

            if (onlySuccess && !onlyFail) filtered = filtered.Where(e => e.Success);
            if (onlyFail && !onlySuccess) filtered = filtered.Where(e => !e.Success);

            var list = filtered.ToList();
            lstHistory.ItemsSource  = list;
            txtHistoryCount.Text    = list.Count.ToString();
        }

        private void TxtHistorySearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void FilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

        private async void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show("Очистить всю историю установок?",
                "Очистка", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            await InstallHistoryService.Instance.ClearAsync();
            await RefreshAsync();
        }

        private async void BtnReinstall_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not HistoryEntry entry) return;

            var catalog = CatalogLoaderService.LoadedCatalog;
            var catalogApp = catalog?.Apps.FirstOrDefault(a => a.Id == entry.AppId);
            if (catalogApp == null)
            {
                MessageBox.Show($"Приложение «{entry.AppName}» не найдено в текущем каталоге.",
                    "Не найдено", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var appInfo = new AppInfo
            {
                Id            = catalogApp.Id,
                DisplayName   = catalogApp.Name,
                Category      = AppCategory.Другое,
                AlternativeId = catalogApp.WingetId,
                InstallerUrls = !string.IsNullOrEmpty(catalogApp.DownloadUrl)
                    ? new List<string> { catalogApp.DownloadUrl }
                    : new(),
                ChocoId = catalogApp.ChocoId,
                ScoopId = catalogApp.ScoopId
            };

            LogMessage?.Invoke($"🔄 Переустановка: {entry.AppName}...");

            using var installer = new InstallationService();
            var cts = new CancellationTokenSource();
            var progress = new Progress<AppInstallProgress>(p =>
                LogMessage?.Invoke($"  {p.Status}"));

            var result = await installer.InstallAppAsync(
                appInfo, new[] { "winget", "msstore" }, cts.Token, progress, "C:\\");

            LogMessage?.Invoke(result.Success
                ? $"✅ {entry.AppName} переустановлен"
                : $"❌ {entry.AppName}: {result.Message}");
        }
    }
}
