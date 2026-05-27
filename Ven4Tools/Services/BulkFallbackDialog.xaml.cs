using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Helpers;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public class FailedAppItem : INotifyPropertyChanged
    {
        public string AppId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string OriginalId { get; set; } = string.Empty;
        public string OriginalUrl { get; set; } = string.Empty;
        public bool IsUserAdded { get; set; }

        public bool HasOriginalId => !string.IsNullOrEmpty(OriginalId);

        public string? SelectedWingetId { get; set; }
        public string? SelectedUrl { get; set; }

        private bool _skip;
        public bool Skip
        {
            get => _skip;
            set
            {
                if (_skip != value)
                {
                    _skip = value;
                    if (_skip)
                    {
                        SearchWinget = false;
                        ReplaceWithUrl = false;
                    }
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShowIgnoreOptions));
                    OnPropertyChanged(nameof(IsEditMode));
                }
            }
        }

        private bool _isSearching;
        public bool IsSearching
        {
            get => _isSearching;
            set
            {
                _isSearching = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasNoResults));
            }
        }

        private bool _searchWinget;
        public bool SearchWinget
        {
            get => _searchWinget;
            set
            {
                if (_searchWinget != value)
                {
                    _searchWinget = value;
                    if (_searchWinget)
                    {
                        Skip = false;
                        ReplaceWithUrl = false;
                        _ = AutoSearchWingetAsync();
                    }
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsSearchMode));
                    OnPropertyChanged(nameof(IsEditMode));
                }
            }
        }

        private bool _replaceWithUrl;
        public bool ReplaceWithUrl
        {
            get => _replaceWithUrl;
            set
            {
                if (_replaceWithUrl != value)
                {
                    _replaceWithUrl = value;
                    if (_replaceWithUrl)
                    {
                        Skip = false;
                        SearchWinget = false;
                    }
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsUrlMode));
                    OnPropertyChanged(nameof(IsEditMode));
                }
            }
        }

        private bool _keepInCatalog = true;
        public bool KeepInCatalog
        {
            get => _keepInCatalog;
            set
            {
                if (_keepInCatalog != value)
                {
                    _keepInCatalog = value;
                    if (_keepInCatalog) RemoveFromCatalog = false;
                    OnPropertyChanged();
                }
            }
        }

        private bool _removeFromCatalog;
        public bool RemoveFromCatalog
        {
            get => _removeFromCatalog;
            set
            {
                if (_removeFromCatalog != value)
                {
                    _removeFromCatalog = value;
                    if (_removeFromCatalog) KeepInCatalog = false;
                    OnPropertyChanged();
                }
            }
        }

        private ObservableCollection<WingetPackage> _searchResults = new();
        public ObservableCollection<WingetPackage> SearchResults
        {
            get => _searchResults;
            set
            {
                _searchResults = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(HasNoResults));
            }
        }

        private WingetPackage? _selectedPackage;
        public WingetPackage? SelectedPackage
        {
            get => _selectedPackage;
            set
            {
                _selectedPackage = value;
                if (value != null) SelectedWingetId = value.Id;
                OnPropertyChanged();
            }
        }

        private string _newUrl = string.Empty;
        public string NewUrl
        {
            get => _newUrl;
            set
            {
                _newUrl = value;
                if (!string.IsNullOrWhiteSpace(value)) SelectedUrl = value;
                OnPropertyChanged();
            }
        }

        public bool HasResults => SearchResults.Any();
        public bool HasNoResults => !HasResults && IsSearchMode && !IsSearching;
        public bool IsEditMode => !SearchWinget && !ReplaceWithUrl && !Skip;
        public bool IsSearchMode => SearchWinget;
        public bool IsUrlMode => ReplaceWithUrl;
        public bool ShowIgnoreOptions => Skip;

        public FailedAppItem() { }

        private async Task AutoSearchWingetAsync()
        {
            if (string.IsNullOrWhiteSpace(DisplayName)) return;

            IsSearching = true;
            SearchResults.Clear();

            try
            {
                var results = await WingetService.SearchAsync(DisplayName);

                SearchResults.Clear();
                foreach (var pkg in results)
                    SearchResults.Add(pkg);

                OnPropertyChanged(nameof(SearchResults));
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(HasNoResults));

                if (results.Count == 1)
                    SelectedPackage = results[0];
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AutoSearch error: {ex.Message}");
            }
            finally
            {
                IsSearching = false;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class BulkFallbackDialog : Window
    {
        public ObservableCollection<FailedAppItem> FailedApps { get; } = new();
        public bool Applied { get; private set; }
        public bool RememberChoices { get; private set; }

        public BulkFallbackDialog(List<(string AppId, string DisplayName, string OriginalId, string OriginalUrl, bool IsUserAdded)> failedApps)
        {
            InitializeComponent();

            foreach (var app in failedApps)
            {
                FailedApps.Add(new FailedAppItem
                {
                    AppId = app.AppId,
                    DisplayName = app.DisplayName,
                    OriginalId = app.OriginalId,
                    OriginalUrl = app.OriginalUrl,
                    IsUserAdded = app.IsUserAdded,
                    Skip = true
                });
            }

            lstFailedApps.ItemsSource = FailedApps;
        }

        private void BtnApplyReplace_Click(object sender, RoutedEventArgs e)
        {
            var selected = FailedApps.Where(x => x.SearchWinget || x.ReplaceWithUrl).ToList();
            var toRemove = FailedApps.Where(x => x.Skip && x.RemoveFromCatalog).ToList();

            if (!selected.Any() && !toRemove.Any())
            {
                MessageBox.Show("Не выбрано ни одной программы для замены или удаления.",
                    "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var app in selected.Where(x => x.SearchWinget))
            {
                if (app.SelectedPackage == null)
                {
                    MessageBox.Show($"Для программы '{app.DisplayName}' не выбран пакет из результатов поиска.",
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            foreach (var app in selected.Where(x => x.ReplaceWithUrl))
            {
                if (string.IsNullOrWhiteSpace(app.NewUrl))
                {
                    MessageBox.Show($"Для программы '{app.DisplayName}' не указана ссылка.",
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!app.NewUrl.StartsWith("http://") && !app.NewUrl.StartsWith("https://"))
                {
                    MessageBox.Show($"Для программы '{app.DisplayName}' указана некорректная ссылка.",
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            if (toRemove.Any())
            {
                var names = string.Join(", ", toRemove.Select(x => x.DisplayName));
                var result = MessageBox.Show(
                    $"Следующие программы будут удалены из каталога:\n{names}\n\nПродолжить?",
                    "Подтверждение удаления",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result == MessageBoxResult.No) return;
            }

            RememberChoices = chkRememberChoices.IsChecked == true;
            Applied = true;
            DialogResult = true;
            Close();
        }

        private void BtnApplySkip_Click(object sender, RoutedEventArgs e)
        {
            foreach (var app in FailedApps)
            {
                if (!app.Skip)
                {
                    app.Skip = true;
                    app.SearchWinget = false;
                    app.ReplaceWithUrl = false;
                }
            }
            RememberChoices = chkRememberChoices.IsChecked == true;
            Applied = true;
            DialogResult = true;
            Close();
        }

        private void BtnSkipAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var app in FailedApps)
            {
                app.Skip = true;
                app.SearchWinget = false;
                app.ReplaceWithUrl = false;
                app.KeepInCatalog = true;
                app.RemoveFromCatalog = false;
            }
            RememberChoices = chkRememberChoices.IsChecked == true;
            Applied = true;
            DialogResult = true;
            Close();
        }
    }
}
