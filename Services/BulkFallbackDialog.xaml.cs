    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Input;

    namespace Ven4Tools.Services
    {
        public class WingetPackage
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Version { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public string DisplayName => $"{Name} ({Id}) — {Version}";
        }

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
                        // Автоматически запускаем поиск при выборе этой опции
                        _ = AutoSearchWinget();
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
                if (value != null)
                {
                    SelectedWingetId = value.Id;
                }
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
                if (!string.IsNullOrWhiteSpace(value))
                {
                    SelectedUrl = value;
                }
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

        private async Task AutoSearchWinget()
{
    if (string.IsNullOrWhiteSpace(DisplayName)) 
    {
        Debug.WriteLine($"AutoSearchWinget: DisplayName пустой");
        return;
    }

    Debug.WriteLine($"AutoSearchWinget: Начинаем поиск для '{DisplayName}'");
    
    IsSearching = true;
    SearchResults.Clear();
    
    try
    {
        var results = await Task.Run(() => SearchWingetPackages(DisplayName));
        
        Debug.WriteLine($"AutoSearchWinget: Найдено {results.Count} результатов");
        
        // Очищаем и добавляем результаты
        SearchResults.Clear();
        foreach (var pkg in results)
        {
            SearchResults.Add(pkg);
            Debug.WriteLine($"  - {pkg.DisplayName}");
        }
        
        // Принудительно обновляем UI
        OnPropertyChanged(nameof(SearchResults));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasNoResults));
        
        // Если найден ровно один результат, автоматически выбираем его
        if (results.Count == 1)
        {
            SelectedPackage = results[0];
            Debug.WriteLine($"AutoSearchWinget: Автоматически выбран {results[0].DisplayName}");
        }
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"Ошибка поиска: {ex.Message}");
    }
    finally
    {
        IsSearching = false;
        Debug.WriteLine($"AutoSearchWinget: Поиск завершён");
    }
}

        private List<WingetPackage> SearchWingetPackages(string query)
        {
            var results = new List<WingetPackage>();

            try
            {
                string args = $"search --name \"{query}\" --source winget --accept-source-agreements";
                
                var psi = new ProcessStartInfo
                {
                    FileName = "winget.exe",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null) return results;

                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    bool headerPassed = false;
                    foreach (var line in lines)
                    {
                        if (!headerPassed && line.Contains("--"))
                        {
                            headerPassed = true;
                            continue;
                        }
                        if (!headerPassed || line.StartsWith("Имя") || string.IsNullOrWhiteSpace(line))
                            continue;

                        var parts = Regex.Split(line, @"\s{2,}");
                        if (parts.Length >= 3)
                        {
                            var pkg = new WingetPackage
                            {
                                Name = parts[0].Trim(),
                                Id = parts[1].Trim(),
                                Version = parts[2].Trim(),
                                Source = parts.Length > 3 ? parts[3].Trim() : "winget"
                            };
                            results.Add(pkg);
                        }
                    }
                }
            }
            catch { }

            return results;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

        public class RelayCommand : ICommand
        {
            private readonly Action<object?> _execute;
            private readonly Func<object?, bool>? _canExecute;

            public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
            {
                _execute = execute;
                _canExecute = canExecute;
            }

            public event EventHandler? CanExecuteChanged
            {
                add { CommandManager.RequerySuggested += value; }
                remove { CommandManager.RequerySuggested -= value; }
            }

            public bool CanExecute(object? parameter) => _canExecute == null || _canExecute(parameter);

            public void Execute(object? parameter) => _execute(parameter);
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

                var wingetSelected = selected.Where(x => x.SearchWinget).ToList();
                foreach (var app in wingetSelected)
                {
                    if (app.SelectedPackage == null)
                    {
                        MessageBox.Show($"Для программы '{app.DisplayName}' не выбран пакет из результатов поиска.", 
                            "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                var urlSelected = selected.Where(x => x.ReplaceWithUrl).ToList();
                foreach (var app in urlSelected)
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
                        
                    if (result == MessageBoxResult.No)
                        return;
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