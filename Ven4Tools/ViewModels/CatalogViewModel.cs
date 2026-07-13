using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    // Оркестратор вкладки каталога — перенесено из CatalogTab.*.cs (AppList/
    // Availability/Catalog/Icons/Install/Presets/Search/UI, ~2700 строк) при
    // переходе на MVVM (2026-07-13). ViewModel ничего не знает про StackPanel/
    // CheckBox — только про данные и команды; CatalogTab.xaml решает, как это
    // отрисовать через DataTemplate/GroupStyle. Реализация проверена прототипом
    // (scratch-проект вне репозитория) перед переносом сюда, включая новую
    // Play-кнопку (см. AppRowViewModel.LaunchCommand + Services/AppLaunchResolver).
    public sealed class CatalogViewModel : INotifyPropertyChanged
    {
        private readonly AppManager _appManager = new();
        private CatalogLoaderService? _catalogLoader;
        private readonly AvailabilityChecker _availabilityChecker = new();
        private readonly InstalledAppsService _installedAppsService = new();
        private readonly FavoritesService _favoritesService = new();
        private InstallationService? _installService;
        private readonly VersionTrackingService _versionTracker = new();
        private readonly string[] _wingetSources = { "winget", "msstore" };
        private readonly CancellationTokenSource _availabilityCts = new();

        public ObservableCollection<AppRowViewModel> Apps { get; } = new();
        public ICollectionView AppsView { get; }
        public ObservableCollection<string> LogLines { get; } = new();
        public ObservableCollection<AppInstallProgress> InstallProgress { get; } = new();
        public ObservableCollection<SearchSuggestionViewModel> Suggestions { get; } = new();
        public ObservableCollection<Preset> Presets { get; } = new();
        public ObservableCollection<DiskOption> AvailableDisks { get; } = new();

        // Ключ — CategoryString (то же значение, что видит GroupDescription),
        // используется CategoryNameToHeaderConverter в CatalogTab.xaml.
        public Dictionary<string, CategoryHeaderViewModel> CategoryHeaders { get; } = new();

        public sealed record DiskOption(string Name, string Space);

        public RelayCommand ToggleFavoriteCommand { get; }
        public RelayCommand SuggestAlternativeCommand { get; }
        public RelayCommand RemoveUserAppCommand { get; }
        public RelayCommand InstallSelectedCommand { get; }
        public RelayCommand CancelInstallCommand { get; }
        public RelayCommand RefreshAvailabilityCommand { get; }
        public RelayCommand RefreshCatalogCommand { get; }
        public RelayCommand RetryLoadCatalogCommand { get; }
        public RelayCommand ClearAllUserAppsCommand { get; }
        public RelayCommand ClearSearchCommand { get; }
        public RelayCommand ToggleFavoritesOnlyCommand { get; }
        public RelayCommand ExportListCommand { get; }
        public RelayCommand ImportListCommand { get; }
        public RelayCommand SavePresetCommand { get; }
        public RelayCommand ApplyPresetCommand { get; }
        public RelayCommand RenamePresetCommand { get; }
        public RelayCommand UpdateAppsPresetCommand { get; }
        public RelayCommand DeletePresetCommand { get; }
        public RelayCommand CheckUpdatesCommand { get; }

        public event Action? SwitchToUpdatesRequested;
        public Func<Window?>? OwnerWindowProvider { get; set; }

        private MasterCatalog? _catalog;
        private Preset? _pendingUpdatePreset;
        private CancellationTokenSource? _installCts;
        private CancellationTokenSource? _searchDebounce;

        private bool _isInstalling;
        public bool IsInstalling
        {
            get => _isInstalling;
            private set
            {
                if (_isInstalling == value) return;
                _isInstalling = value;
                OnPropertyChanged(nameof(IsInstalling));
                // Не связано с прямым UI-событием (клик мышью/клавиатура), которое
                // CommandManager.RequerySuggested перехватывает сам — без явного
                // вызова кнопки могли оставаться закэшированно enabled/disabled.
                InstallSelectedCommand.RaiseCanExecuteChanged();
                CancelInstallCommand.RaiseCanExecuteChanged();
            }
        }
        public string SelectedInstallDrive { get; private set; } = "C:\\";

        public CatalogViewModel()
        {
            AppsView = CollectionViewSource.GetDefaultView(Apps);
            AppsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AppRowViewModel.CategoryString)));
            AppsView.SortDescriptions.Add(new SortDescription(nameof(AppRowViewModel.CategoryString), ListSortDirection.Ascending));
            AppsView.SortDescriptions.Add(new SortDescription(nameof(AppRowViewModel.DisplayName), ListSortDirection.Ascending));
            AppsView.Filter = RowFilter;

            ToggleFavoriteCommand = new RelayCommand(p =>
            {
                if (p is not AppRowViewModel row) return;
                _favoritesService.Toggle(row.AppId);
                row.IsFavorite = _favoritesService.IsFavorite(row.AppId);
                if (ShowFavoritesOnly) AppsView.Refresh();
            });

            SuggestAlternativeCommand = new RelayCommand(async p =>
            {
                if (p is AppRowViewModel row) await SuggestAlternativeAsync(row);
            });

            RemoveUserAppCommand = new RelayCommand(p =>
            {
                if (p is AppRowViewModel row) RemoveUserApp(row);
            });

            InstallSelectedCommand = new RelayCommand(async _ => await InstallSelectedAsync(),
                _ => !IsInstalling && Apps.Any(a => a.IsSelected && a.IsSelectable));

            CancelInstallCommand = new RelayCommand(_ =>
            {
                if (_installCts == null) return;
                if (MessageBox.Show("Вы действительно хотите прервать установку?", "Подтверждение отмены",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _installCts.Cancel();
            }, _ => IsInstalling);

            RefreshAvailabilityCommand = new RelayCommand(async _ => await RefreshAvailabilityAsync(),
                _ => !_isCheckingAvailability);

            RefreshCatalogCommand = new RelayCommand(async _ => await RefreshCatalogAsync());
            RetryLoadCatalogCommand = new RelayCommand(async _ => await LoadAsync());

            ClearAllUserAppsCommand = new RelayCommand(_ =>
            {
                if (MessageBox.Show("Вы действительно хотите удалить ВСЕ пользовательские приложения?",
                        "Полная очистка", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                _appManager.ClearUserApps();
                foreach (var row in Apps.Where(a => a.IsUserAdded).ToList())
                    Apps.Remove(row);
                Log("✅ Пользовательские приложения очищены");
            });

            ClearSearchCommand = new RelayCommand(_ => SearchText = "");
            ToggleFavoritesOnlyCommand = new RelayCommand(_ => ShowFavoritesOnly = !ShowFavoritesOnly);

            ExportListCommand = new RelayCommand(_ => ExportList());
            ImportListCommand = new RelayCommand(_ => ImportList());

            SavePresetCommand = new RelayCommand(async _ => await SavePresetAsync(),
                _ => Apps.Any(a => a.IsSelected));
            ApplyPresetCommand = new RelayCommand(p => { if (p is Preset preset) ApplyPreset(preset); });
            RenamePresetCommand = new RelayCommand(async p => { if (p is Preset preset) await RenamePresetAsync(preset); });
            UpdateAppsPresetCommand = new RelayCommand(p => { if (p is Preset preset) BeginUpdatePresetComposition(preset); });
            DeletePresetCommand = new RelayCommand(async p => { if (p is Preset preset) await DeletePresetAsync(preset); });

            CheckUpdatesCommand = new RelayCommand(_ => SwitchToUpdatesRequested?.Invoke());

            LoadAvailableDisks();
        }

        // ── Поиск / фильтры ─────────────────────────────────────────────────────

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetField(ref _searchText, value))
                {
                    OnPropertyChanged(nameof(HasSearchText));
                    AppsView.Refresh();
                    _searchDebounce?.Cancel();
                    if (value.Length >= 2 && AppsView.Cast<object>().Count() == 0)
                    {
                        _searchDebounce = new CancellationTokenSource();
                        _ = RunSearchSuggestionsAsync(value, _searchDebounce.Token);
                    }
                    else
                    {
                        ShowSuggestionsPanel = false;
                        Suggestions.Clear();
                    }
                }
            }
        }

        public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

        private bool _showFavoritesOnly;
        public bool ShowFavoritesOnly
        {
            get => _showFavoritesOnly;
            set
            {
                if (SetField(ref _showFavoritesOnly, value))
                {
                    OnPropertyChanged(nameof(FavoritesOnlyBrush));
                    AppsView.Refresh();
                }
            }
        }

        // btnFavoritesOnly — обычная Button (не ToggleButton): ClientUITests
        // (Phase1CatalogRemainingTests) вызывает её через AsButton().Invoke(),
        // который требует Invoke-паттерн UIA, а не Toggle. Состояние переключается
        // командой, а не IsChecked.
        public System.Windows.Media.Brush FavoritesOnlyBrush => ShowFavoritesOnly
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 215, 0))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 100, 100));

        private bool _showSuggestionsPanel;
        public bool ShowSuggestionsPanel
        {
            get => _showSuggestionsPanel;
            set => SetField(ref _showSuggestionsPanel, value);
        }

        private string _suggestionsStatus = "";
        public string SuggestionsStatus
        {
            get => _suggestionsStatus;
            set => SetField(ref _suggestionsStatus, value);
        }

        private bool RowFilter(object obj)
        {
            if (obj is not AppRowViewModel row) return false;
            if (!row.MatchesProfile) return false;
            if (ProfileService.Current.HideInstalled && row.IsInstalled) return false;
            if (ShowFavoritesOnly && !row.IsFavorite) return false;
            if (string.IsNullOrWhiteSpace(SearchText)) return true;
            return row.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
        }

        public void ApplyProfileFilters()
        {
            int modeLevel = ProfileService.Current.CatalogMode switch { "basic" => 0, "extended" => 1, _ => 2 };
            foreach (var row in Apps)
            {
                int appLevel = row.Profile switch { "extended" => 1, "full" => 2, _ => 0 };
                row.MatchesProfile = appLevel <= modeLevel;
            }
            AppsView.Refresh();
        }

        private async Task RunSearchSuggestionsAsync(string query, CancellationToken token)
        {
            try
            {
                await Task.Delay(600, token);
                ShowSuggestionsPanel = true;
                Suggestions.Clear();
                SuggestionsStatus = "⏳ Поиск по источникам...";

                var wingetTask = WingetService.SearchAsync(query, token);
                var chocoTask = PackageManagerService.SearchChocoAsync(query, token);
                await Task.WhenAll(wingetTask, chocoTask);
                if (token.IsCancellationRequested) return;

                var winget = wingetTask.Result;
                var choco = chocoTask.Result;
                if (winget.Count == 0 && choco.Count == 0)
                {
                    SuggestionsStatus = $"😕 Ничего не найдено по запросу «{query}» ни в одном источнике";
                    return;
                }
                SuggestionsStatus = "";

                foreach (var pkg in winget)
                {
                    var captureId = pkg.Id; var captureName = pkg.Name;
                    Suggestions.Add(new SearchSuggestionViewModel(pkg.Name, $"winget:{pkg.Id}", "📦 Winget",
                        () => AddWingetSuggestion(captureName, captureId)));
                }
                foreach (var (id, name, ver) in choco)
                {
                    var captureId = id;
                    Suggestions.Add(new SearchSuggestionViewModel(name.Length > 0 ? name : id, $"v{ver}", "🍫 Chocolatey",
                        () => AddChocoSuggestion(captureId)));
                }
            }
            catch (OperationCanceledException) { }
            catch (TimeoutException)
            {
                if (!token.IsCancellationRequested)
                    SuggestionsStatus = "⚠ Winget не ответил вовремя";
            }
        }

        private void AddWingetSuggestion(string name, string id)
        {
            if (_appManager.GetAppById(id) != null) { Log($"ℹ️ {name} уже есть в списке"); return; }
            var app = new AppInfo { Id = id, DisplayName = name, Category = AppCategory.Другое, AlternativeId = id, IsUserAdded = true, SilentArgs = "" };
            AddUserApp(app);
            Log($"➕ Добавлено из winget: {name} ({id})");
            SearchText = "";
        }

        private void AddChocoSuggestion(string id)
        {
            string userId = $"User.{id}";
            if (_appManager.GetAppById(userId) != null) { Log($"ℹ️ {id} уже есть в списке"); return; }
            var app = new AppInfo { Id = userId, DisplayName = id, Category = AppCategory.Другое, ChocoId = id, IsUserAdded = true };
            AddUserApp(app);
            Log($"➕ Добавлено из Chocolatey: {id}");
            SearchText = "";
        }

        // ── Загрузка каталога ───────────────────────────────────────────────────

        private string _statusText = "⏳ Загрузка каталога...";
        public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }

        // Отдельно от StatusText (статус загрузки каталога) — раньше это были два
        // разных TextBlock (txtLoadingStatus сверху и txtOverallStatus в панели
        // установки справа), их нельзя было схлопывать в одно свойство.
        private string _installStatusText = "Готов";
        public string InstallStatusText { get => _installStatusText; set => SetField(ref _installStatusText, value); }

        private bool _catalogErrorVisible;
        public bool CatalogErrorVisible { get => _catalogErrorVisible; set => SetField(ref _catalogErrorVisible, value); }

        private string _catalogErrorDetail = "";
        public string CatalogErrorDetail { get => _catalogErrorDetail; set => SetField(ref _catalogErrorDetail, value); }

        public async Task LoadAsync()
        {
            CatalogErrorVisible = false;
            StatusText = "⏳ Загрузка каталога...";
            _catalogLoader ??= new CatalogLoaderService();
            _installService ??= new InstallationService();

            try
            {
                var catalog = CatalogLoaderService.LoadedCatalog ?? await _catalogLoader.LoadCatalogAsync();
                if (catalog == null)
                {
                    StatusText = "❌ Ошибка: не удалось загрузить каталог";
                    CatalogErrorDetail = "Нет подключения к интернету или CDN недоступен.\nПроверьте сеть и нажмите «Повторить загрузку».";
                    CatalogErrorVisible = true;
                    return;
                }

                _catalog = catalog;
                SyncCatalogToAppManager();
                _appManager.LoadAlternativeSources();

                string sourceText = catalog.Source switch
                {
                    "hosting"  => "🏠 Каталог загружен с ven4tools.ru",
                    "cdn"      => "🌐 Каталог загружен с CDN (cdn.ven4tools.ru)",
                    "online"   => "🌐 Каталог загружен с GitHub",
                    "cache"    => "💾 Каталог из кэша (интернет недоступен)",
                    "embedded" => "📀 Встроенный каталог (минимальный набор)",
                    _          => "❓ Неизвестный источник"
                };
                Log(sourceText);
                Log($"Загружено приложений: {catalog.Apps.Count}");

                BuildRows();
                BuildCategoryHeaders();
                ApplyCategorySourceHeaders();
                ApplyProfileFilters();

                StatusText = $"Загружено {Apps.Count} приложений";
                _ = InitialLoadAvailabilityAsync();
                _ = RefreshPresetsAsync();
                foreach (var row in Apps) _ = row.LoadIconAsync();
            }
            catch (Exception ex)
            {
                StatusText = "❌ Ошибка загрузки";
                Log($"Ошибка загрузки каталога: {ex.Message}");
            }
        }

        private async Task RefreshCatalogAsync()
        {
            Log("🔄 Обновление каталога...");
            try
            {
                var catalogCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "master.json");
                try
                {
                    if (File.Exists(catalogCachePath)) File.Delete(catalogCachePath);
                    if (File.Exists(catalogCachePath + ".sig")) File.Delete(catalogCachePath + ".sig");
                }
                catch { }

                _catalogLoader ??= new CatalogLoaderService();
                var loaded = await _catalogLoader.LoadCatalogAsync();
                if (loaded == null) { Log("❌ Ошибка: не удалось загрузить каталог"); return; }

                _catalog = loaded;
                SyncCatalogToAppManager();
                _appManager.LoadAlternativeSources();
                _appManager.ApplyAlternativesToCatalog(_catalog);

                BuildRows();
                BuildCategoryHeaders();
                ApplyCategorySourceHeaders();
                ApplyProfileFilters();
                Log($"📦 Загружено приложений: {_catalog.Apps.Count}");
                Log("✅ Каталог успешно обновлён");

                _ = InitialLoadAvailabilityAsync();
            }
            catch (Exception ex) { Log($"❌ Ошибка: {ex.Message}"); }
        }

        private void SyncCatalogToAppManager()
        {
            if (_catalog == null) return;
            foreach (var catalogApp in _catalog.Apps)
            {
                var existing = _appManager.GetAppById(catalogApp.Id);
                if (existing == null)
                {
                    var appInfo = new AppInfo
                    {
                        Id = catalogApp.Id,
                        DisplayName = catalogApp.Name,
                        Category = AppCategoryHelper.Parse(catalogApp.Category),
                        InstallerUrls = !string.IsNullOrEmpty(catalogApp.DownloadUrl)
                            ? new List<string> { catalogApp.DownloadUrl } : new List<string>(),
                        AlternativeId = catalogApp.WingetId,
                        RequiredSpaceMB = ParseSizeToMB(catalogApp.Size),
                        IsUserAdded = false,
                        ChocoId = catalogApp.ChocoId,
                        Sha256 = catalogApp.Sha256
                    };
                    if (!string.IsNullOrEmpty(catalogApp.SilentArgs)) appInfo.SilentArgs = catalogApp.SilentArgs;
                    _appManager.AddCatalogApp(appInfo);
                }
                else if (!existing.IsUserAdded)
                {
                    if (!string.IsNullOrEmpty(catalogApp.DownloadUrl)) existing.InstallerUrls = new List<string> { catalogApp.DownloadUrl };
                    if (!string.IsNullOrEmpty(catalogApp.WingetId)) existing.AlternativeId = catalogApp.WingetId;
                    existing.ChocoId = catalogApp.ChocoId;
                    existing.Sha256 = catalogApp.Sha256;
                    if (!string.IsNullOrEmpty(catalogApp.SilentArgs)) existing.SilentArgs = catalogApp.SilentArgs;
                }
            }
        }

        private static int ParseSizeToMB(string size)
        {
            if (string.IsNullOrEmpty(size)) return 100;
            try
            {
                var match = System.Text.RegularExpressions.Regex.Match(size, @"(\d+(?:\.\d+)?)");
                if (match.Success && double.TryParse(match.Value, out double value))
                    return size.Contains("GB", StringComparison.OrdinalIgnoreCase) ? (int)(value * 1024) : (int)value;
            }
            catch { }
            return 100;
        }

        private void BuildRows()
        {
            var existingUserRows = Apps.Where(a => a.IsUserAdded).ToList();
            Apps.Clear();

            if (_catalog != null)
            {
                foreach (var catalogApp in _catalog.Apps)
                {
                    var appInfo = _appManager.GetAppById(catalogApp.Id);
                    if (appInfo == null) continue;
                    var row = new AppRowViewModel(appInfo)
                    {
                        IconUrl = catalogApp.IconUrl,
                        Profile = catalogApp.Profile
                    };
                    row.IsFavorite = _favoritesService.IsFavorite(row.AppId);
                    row.SelectionChanged += () => OnPropertyChanged(nameof(SelectedCount));
                    Apps.Add(row);
                }
            }

            foreach (var row in existingUserRows) Apps.Add(row);
            foreach (var appInfo in _appManager.GetAllApps().Where(a => a.IsUserAdded))
            {
                if (Apps.Any(a => a.AppId == appInfo.Id)) continue;
                var row = new AppRowViewModel(appInfo) { IsFavorite = _favoritesService.IsFavorite(appInfo.Id) };
                row.SelectionChanged += () => OnPropertyChanged(nameof(SelectedCount));
                Apps.Add(row);
                if (!string.IsNullOrEmpty(appInfo.AlternativeId))
                    _ = FetchVersionsForRowAsync(row);
            }
        }

        private void BuildCategoryHeaders()
        {
            CategoryHeaders.Clear();
            foreach (var cat in Enum.GetValues<AppCategory>())
            {
                string label = new AppInfo { Category = cat }.CategoryString;
                CategoryHeaders[label] = new CategoryHeaderViewModel(cat.ToString(), label);
            }
        }

        public void ApplyCategorySourceHeaders()
        {
            bool perCategory = SourceOrderService.Current.Mode == "per_category";
            foreach (var header in CategoryHeaders.Values)
                header.ShowCombo = perCategory;
        }

        // ── Доступность / установленность / Play ───────────────────────────────

        private bool _isCheckingAvailability;

        // Соответствует RefreshAvailability_Click оригинала: только проверка
        // доступности, лог "Проверка завершена" и снятие флага сразу после —
        // btnRefreshAvailability должна разблокироваться сразу, без ожидания
        // версий/статуса установки. Не объединять с InitialLoadAvailabilityAsync
        // ниже — иначе кнопка остаётся disabled дольше, чем показывает лог
        // (ровно это ловит AuditFixesCatalogFlowTests.Полный_Проход_Каталог).
        private async Task RefreshAvailabilityAsync()
        {
            _isCheckingAvailability = true;
            // CommandManager.RequerySuggested (см. RelayCommand) перепроверяет CanExecute
            // только на стандартные UI-события (фокус, клавиатура/мышь) — простая смена
            // приватного поля этого не вызывает. Без явного RaiseCanExecuteChanged кнопка
            // могла оставаться закэшированно disabled уже после того, как флаг снят
            // (см. AuditFixesCatalogFlowTests.Полный_Проход_Каталог — ElementNotEnabledException).
            RefreshAvailabilityCommand.RaiseCanExecuteChanged();
            try
            {
                using var sem = new SemaphoreSlim(5);
                var tasks = Apps.Select(row => CheckOneAvailabilityAsync(row, sem)).ToList();
                await Task.WhenAll(tasks);

                Log($"✅ Проверка завершена: {Apps.Count(a => a.Availability == AppRowViewModel.RowAvailability.Available)} доступно, " +
                    $"{Apps.Count(a => a.Availability == AppRowViewModel.RowAvailability.Unavailable)} недоступно");
            }
            finally
            {
                _isCheckingAvailability = false;
                RefreshAvailabilityCommand.RaiseCanExecuteChanged();
            }
        }

        // Путь первичной загрузки каталога (и смены порядка источников) — после
        // самой проверки доступности ЕЩЁ продолжает версиями/статусом установки,
        // как оригинальный LoadApps()/OnSourceOrderChanged, но это НЕ должно
        // держать btnRefreshAvailability заблокированной все эти секунды.
        private async Task InitialLoadAvailabilityAsync()
        {
            await RefreshAvailabilityAsync();
            await FetchVersionsPhase2Async();
            await UpdateInstalledStatusAsync();
        }

        private async Task CheckOneAvailabilityAsync(AppRowViewModel row, SemaphoreSlim sem)
        {
            await sem.WaitAsync();
            try
            {
                var (status, _) = await _availabilityChecker.CheckAppAvailabilityWithSize(row.App);
                row.Availability = status switch
                {
                    AvailabilityChecker.AvailabilityStatus.Available   => AppRowViewModel.RowAvailability.Available,
                    AvailabilityChecker.AvailabilityStatus.Unavailable => AppRowViewModel.RowAvailability.Unavailable,
                    _                                                  => AppRowViewModel.RowAvailability.Unknown
                };
            }
            catch { row.Availability = AppRowViewModel.RowAvailability.Unknown; }
            finally { sem.Release(); }
        }

        private async Task FetchVersionsForRowAsync(AppRowViewModel row)
        {
            if (string.IsNullOrEmpty(row.App.AlternativeId)) return;
            var versions = await WingetVersionsService.FetchVersionsAsync(row.App.AlternativeId);
            if (versions.Count == 0) return;
            Application.Current?.Dispatcher.Invoke(() =>
            {
                row.VersionOptions.Clear();
                row.VersionOptions.Add("Последняя");
                foreach (var v in versions) row.VersionOptions.Add(v);
                row.SelectedVersionOption = "Последняя";
                row.IsVersionComboEnabled = true;
            });
        }

        private async Task FetchVersionsPhase2Async()
        {
            using var sem = new SemaphoreSlim(3);
            var tasks = Apps
                .Where(r => !string.IsNullOrEmpty(r.App.AlternativeId) && r.Availability != AppRowViewModel.RowAvailability.Unavailable)
                .Select(row => Task.Run(async () =>
                {
                    await sem.WaitAsync();
                    try { await FetchVersionsForRowAsync(row); }
                    finally { sem.Release(); }
                }));
            await Task.WhenAll(tasks);
        }

        private async Task UpdateInstalledStatusAsync()
        {
            await _installedAppsService.RefreshAsync();
            AppLaunchResolver.InvalidateCache();

            int installed = 0, outdated = 0, launchable = 0;
            foreach (var row in Apps)
            {
                string wingetId = !string.IsNullOrEmpty(row.App.AlternativeId) ? row.App.AlternativeId! : row.AppId;
                bool isInstalled = _installedAppsService.IsInstalled(wingetId);
                row.IsInstalled = isInstalled;

                if (isInstalled)
                {
                    string version = _installedAppsService.GetInstalledVersion(wingetId);
                    row.InstalledVersion = version;
                    row.HasUpdate = !string.IsNullOrEmpty(version) && row.VersionOptions.Count > 1 && version != row.VersionOptions[1];
                    row.LaunchPath = AppLaunchResolver.TryResolve(row.DisplayName);
                    installed++;
                    if (row.HasUpdate) outdated++;
                    if (row.LaunchPath != null) launchable++;
                }
                else
                {
                    row.InstalledVersion = null;
                    row.HasUpdate = false;
                    row.LaunchPath = null;
                }
            }

            if (installed > 0) Log($"📦 Уже установлено: {installed} из {Apps.Count} приложений (кнопка запуска — у {launchable})");
            if (outdated > 0) Log($"🆙 Доступно обновлений: {outdated}");
            if (ProfileService.Current.HideInstalled) AppsView.Refresh();
        }

        private async Task SuggestAlternativeAsync(AppRowViewModel row)
        {
            Log($"🔍 Поиск альтернативы для: {row.DisplayName}");
            var owner = OwnerWindowProvider?.Invoke();
            var dialog = new AlternativeSourceDialog(row.DisplayName) { Owner = owner };
            if (dialog.ShowDialog() != true) return;

            if (dialog.SelectedPackage != null)
            {
                _appManager.SaveAlternativeSource(row.AppId, dialog.SelectedPackage.Id, null, dialog.UseWingetFirst);
                Log($"✅ Сохранён Winget ID: {dialog.SelectedPackage.Id} для {row.DisplayName}");
            }
            else if (!string.IsNullOrEmpty(dialog.CustomUrl))
            {
                _appManager.SaveAlternativeSource(row.AppId, null, dialog.CustomUrl, dialog.UseUrlFirst);
                Log($"✅ Сохранена ссылка: {dialog.CustomUrl} для {row.DisplayName}");
            }
            await Task.Delay(500);
            using var sem = new SemaphoreSlim(1);
            await CheckOneAvailabilityAsync(row, sem);
        }

        // ── Установка ────────────────────────────────────────────────────────────

        public int SelectedCount => Apps.Count(a => a.IsSelected);

        private double _overallProgressPercentage;
        public double OverallProgressPercentage
        {
            get => _overallProgressPercentage;
            set => SetField(ref _overallProgressPercentage, value);
        }

        private async Task InstallSelectedAsync()
        {
            var selected = Apps.Where(a => a.IsSelected && a.IsSelectable).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Выберите хотя бы одну программу!", "Ven4Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (InstallationService.IsBusy)
            {
                MessageBox.Show("Дождитесь завершения текущей установки, затем повторите попытку.",
                    "Установка занята", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            InstallProgress.Clear();
            OverallProgressPercentage = 0;
            IsInstalling = true;

            if (selected.Count >= 2)
            {
                var rpAnswer = MessageBox.Show(
                    $"Будет установлено {selected.Count} приложений.\n\nСоздать точку восстановления Windows перед установкой?",
                    "Точка восстановления", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (rpAnswer == MessageBoxResult.Cancel)
                {
                    IsInstalling = false;
                    return;
                }
                if (rpAnswer == MessageBoxResult.Yes)
                {
                    Log("🛡️ Создаю точку восстановления...");
                    bool ok = await CreateRestorePointAsync();
                    Log(ok ? "✅ Точка восстановления создана" : "⚠️ Точка восстановления не создана (можно продолжать)");
                }
            }

            _installCts = new CancellationTokenSource();
            var token = _installCts.Token;
            int completed = 0, failed = 0;
            InstallStatusText = $"⏳ Установка 0/{selected.Count}...";

            var progress = new Progress<AppInstallProgress>(p =>
            {
                var existing = InstallProgress.FirstOrDefault(x => x.AppId == p.AppId);
                if (existing != null) { existing.Status = p.Status; existing.Percentage = p.Percentage; }
                else InstallProgress.Add(p);

                OverallProgressPercentage = InstallProgress.All(x => x.Percentage >= 100 || x.Status.Contains("Ошибка") || x.Status.Contains("Отменено"))
                    ? 100
                    : InstallProgress.Average(x => x.Percentage);
            });

            var pmConsentCache = new Dictionary<string, bool>();
            using var pmConsentLock = new SemaphoreSlim(1, 1);
            async Task<bool> ConfirmPmInstall(string pmName)
            {
                await pmConsentLock.WaitAsync();
                try
                {
                    if (pmConsentCache.TryGetValue(pmName, out bool cached)) return cached;
                    bool consented = MessageBox.Show(
                        $"Для установки приложения требуется {pmName}, который сейчас не установлен.\n\n" +
                        $"Разрешить автоматическую установку {pmName}?", $"Установка {pmName}",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                    pmConsentCache[pmName] = consented;
                    return consented;
                }
                finally { pmConsentLock.Release(); }
            }

            var tasks = selected.Select(row => Task.Run(async () =>
            {
                await InstallationService.InstallSemaphore.WaitAsync();
                try
                {
                    if (token.IsCancellationRequested) return;
                    var result = await _installService!.InstallAppAsync(
                        row.App, _wingetSources, token, progress, SelectedInstallDrive, row.PinnedVersion, ConfirmPmInstall);
                    if (result.Success)
                    {
                        completed++;
                        if (row.PinnedVersion != null && row.VersionOptions.Count > 1)
                            _versionTracker.TrackInstall(row.AppId, row.PinnedVersion, row.VersionOptions[1]);
                        row.JustInstalled = true;
                    }
                    else failed++;
                    InstallStatusText = $"⏳ Установка: {completed + failed}/{selected.Count} (✅ {completed} | ❌ {failed})";
                }
                finally { InstallationService.InstallSemaphore.Release(); }
            }, token));

            try
            {
                await Task.WhenAll(tasks);
                InstallStatusText = $"✅ Установка завершена. Успешно: {completed}, ошибок: {failed}";
                Log(InstallStatusText);
                await UpdateInstalledStatusAsync();
            }
            catch (OperationCanceledException) { InstallStatusText = "⏹️ Установка отменена"; }
            finally
            {
                IsInstalling = false;
                _installCts?.Dispose();
                _installCts = null;
                UpdateSpaceStatus();
            }
        }

        private static async Task<bool> CreateRestorePointAsync()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"Checkpoint-Computer -Description 'Ven4Tools — перед установкой' -RestorePointType MODIFY_SETTINGS\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return false;
                await Task.WhenAll(p.StandardOutput.ReadToEndAsync(), p.StandardError.ReadToEndAsync());
                await p.WaitForExitAsync();
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        // ── Пользовательские приложения ─────────────────────────────────────────

        public void AddLocalInstallerApp(AppInfo app)
        {
            _appManager.AddUserApp(app);
            AddUserApp(app);
            Log($"📦 Локальный установщик добавлен: {app.DisplayName}");
        }

        private void AddUserApp(AppInfo app)
        {
            if (!app.IsUserAdded) _appManager.AddUserApp(app);
            var row = new AppRowViewModel(app) { IsFavorite = _favoritesService.IsFavorite(app.Id) };
            row.SelectionChanged += () => OnPropertyChanged(nameof(SelectedCount));
            Apps.Add(row);
            if (!string.IsNullOrEmpty(app.AlternativeId)) _ = FetchVersionsForRowAsync(row);
        }

        private void RemoveUserApp(AppRowViewModel row)
        {
            if (MessageBox.Show("Удалить приложение из списка?", "Подтверждение",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _appManager.RemoveUserApp(row.AppId);
            Apps.Remove(row);
            Log("🗑️ Удалено пользовательское приложение");
        }

        // ── Диск установки ──────────────────────────────────────────────────────

        private string _spaceStatus = "";
        public string SpaceStatus { get => _spaceStatus; set => SetField(ref _spaceStatus, value); }

        private DiskOption? _selectedDisk;
        public DiskOption? SelectedDisk
        {
            get => _selectedDisk;
            set
            {
                if (SetField(ref _selectedDisk, value) && value != null)
                {
                    SelectedInstallDrive = value.Name + "\\";
                    UpdateDiskSpaceInfo();
                    UpdateSpaceStatus();
                }
            }
        }

        private void LoadAvailableDisks()
        {
            try
            {
                string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                    .Select(d => new DiskOption(d.RootDirectory.FullName.TrimEnd('\\'),
                        $"{d.Name.TrimEnd('\\')} ({d.AvailableFreeSpace / 1024 / 1024 / 1024:F1} ГБ свободно)"))
                    .ToList();

                AvailableDisks.Clear();
                foreach (var d in drives) AvailableDisks.Add(d);

                var systemDisk = drives.FirstOrDefault(d => d.Name == systemDrive);
                SelectedDisk = systemDisk ?? drives.FirstOrDefault();
                UpdateDiskSpaceInfo();
            }
            catch (Exception ex) { Log($"⚠️ Ошибка получения списка дисков: {ex.Message}"); }
        }

        private void UpdateDiskSpaceInfo()
        {
            try
            {
                string disk = SelectedInstallDrive.TrimEnd('\\');
                var drive = new DriveInfo(disk);
                if (drive.IsReady)
                    SpaceStatus = $"💾 Диск {disk} | Свободно: {drive.AvailableFreeSpace / 1024 / 1024 / 1024} ГБ / {drive.TotalSize / 1024 / 1024 / 1024} ГБ";
            }
            catch (Exception ex) { Log($"⚠️ Ошибка обновления информации о диске: {ex.Message}"); }
        }

        private async void UpdateSpaceStatus()
        {
            try
            {
                var selected = Apps.Where(a => a.IsSelected).ToList();
                using var sem = new SemaphoreSlim(5);
                long totalRequired = 0;
                var lockObj = new object();

                await Task.WhenAll(selected.Select(async row =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        var result = await _availabilityChecker.CheckAppAvailabilityWithSize(row.App);
                        long mb = result.Status == AvailabilityChecker.AvailabilityStatus.Available ? result.SizeMB : 100;
                        lock (lockObj) { totalRequired += mb; }
                    }
                    finally { sem.Release(); }
                }));

                string disk = SelectedInstallDrive.TrimEnd('\\');
                var drive = new DriveInfo(disk);
                if (drive.IsReady)
                {
                    long availableMB = drive.AvailableFreeSpace / 1024 / 1024;
                    SpaceStatus = availableMB >= totalRequired
                        ? $"💾 Диск {disk} | Требуется: ~{totalRequired} МБ | Доступно: {availableMB} МБ ✅"
                        : $"💾 Диск {disk} | Требуется: ~{totalRequired} МБ | Доступно: {availableMB} МБ ❌ Мало места!";
                }
            }
            catch (Exception ex) { Log($"⚠️ Ошибка проверки места: {ex.Message}"); }
        }

        // ── Пресеты ──────────────────────────────────────────────────────────────

        private bool _presetsEmpty = true;
        public bool PresetsEmpty { get => _presetsEmpty; set => SetField(ref _presetsEmpty, value); }

        private string _savePresetLabel = "💾 Сохранить выбор";
        public string SavePresetLabel { get => _savePresetLabel; set => SetField(ref _savePresetLabel, value); }

        private async Task RefreshPresetsAsync()
        {
            _pendingUpdatePreset = null;
            SavePresetLabel = "💾 Сохранить выбор";
            var list = await PresetService.LoadAsync();
            Presets.Clear();
            foreach (var p in list) Presets.Add(p);
            PresetsEmpty = Presets.Count == 0;
        }

        private async Task SavePresetAsync()
        {
            if (_pendingUpdatePreset != null)
            {
                var updating = _pendingUpdatePreset;
                _pendingUpdatePreset = null;
                SavePresetLabel = "💾 Сохранить выбор";

                var selectedIds = Apps.Where(a => a.IsSelected).Select(a => a.AppId).ToList();
                if (selectedIds.Count == 0) return;
                var previous = updating.Apps;
                updating.Apps = selectedIds;
                bool ok = await PresetService.UpdateAsync(updating);
                if (ok) updating.RaiseAppCountChanged(); else updating.Apps = previous;
                Log(ok ? $"✅ Состав пресета «{updating.Name}» обновлён ({selectedIds.Count} прил.)"
                       : $"❌ Не удалось обновить состав пресета «{updating.Name}»");
                return;
            }

            var selected = Apps.Where(a => a.IsSelected).Select(a => a.AppId).ToList();
            if (selected.Count == 0) return;

            var owner = OwnerWindowProvider?.Invoke();
            var dlg = new Views.PresetSaveDialog(selected.Count) { Owner = owner };
            if (dlg.ShowDialog() != true) return;

            var preset = new Preset { Name = dlg.PresetName, Description = dlg.PresetDescription, Apps = selected };
            var saved = await PresetService.SaveAsync(preset);
            if (saved == null) { Log("❌ Не удалось сохранить пресет"); return; }
            Presets.Insert(0, saved);
            PresetsEmpty = false;
            Log($"✅ Пресет «{saved.Name}» сохранён ({selected.Count} прил.)");
        }

        private void ApplyPreset(Preset preset)
        {
            int applied = 0;
            foreach (var id in preset.Apps)
            {
                var row = Apps.FirstOrDefault(a => a.AppId == id);
                if (row != null && row.IsSelectable)
                {
                    row.IsSelected = true;
                    applied++;
                }
            }
            Log($"📋 Пресет «{preset.Name}» применён: {applied} из {preset.Apps.Count} приложений отмечено");
        }

        private async Task RenamePresetAsync(Preset preset)
        {
            var owner = OwnerWindowProvider?.Invoke();
            var dlg = new Views.PresetSaveDialog(preset.Name, preset.Description) { Owner = owner };
            if (dlg.ShowDialog() != true) return;

            string oldName = preset.Name, oldDesc = preset.Description;
            preset.Name = dlg.PresetName;
            preset.Description = dlg.PresetDescription;
            bool ok = await PresetService.UpdateAsync(preset);
            if (ok) preset.RaiseNameChanged();
            else { preset.Name = oldName; preset.Description = oldDesc; }
            Log(ok ? $"✅ Пресет переименован: «{preset.Name}»" : $"❌ Не удалось переименовать пресет «{oldName}»");
        }

        private void BeginUpdatePresetComposition(Preset preset)
        {
            ApplyPreset(preset);
            _pendingUpdatePreset = preset;
            SavePresetLabel = $"↻ Обновить «{preset.Name}»";
        }

        private async Task DeletePresetAsync(Preset preset)
        {
            if (MessageBox.Show($"Удалить пресет «{preset.Name}»?", "Пресеты",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (_pendingUpdatePreset == preset)
            {
                _pendingUpdatePreset = null;
                SavePresetLabel = "💾 Сохранить выбор";
            }
            await PresetService.DeleteAsync(preset);
            Presets.Remove(preset);
            PresetsEmpty = Presets.Count == 0;
            Log($"🗑️ Пресет «{preset.Name}» удалён");
        }

        // ── Экспорт/импорт списка ────────────────────────────────────────────────

        private void ExportList()
        {
            var selected = Apps.Where(a => a.IsSelected).Select(a => a.AppId).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Нет выбранных приложений для экспорта.", "Экспорт", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Экспорт списка приложений",
                Filter = "JSON файлы (*.json)|*.json",
                FileName = $"ven4tools_list_{DateTime.Now:yyyyMMdd_HHmm}.json",
                DefaultExt = ".json"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var payload = new { exported_at = DateTime.Now.ToString("o"), app_ids = selected.OrderBy(id => id).ToList() };
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(payload, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(dlg.FileName, json, System.Text.Encoding.UTF8);
                Log($"📤 Экспорт: {selected.Count} приложений → {Path.GetFileName(dlg.FileName)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при сохранении:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImportList()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Импорт списка приложений", Filter = "JSON файлы (*.json)|*.json" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                string json = File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8);
                var doc = Newtonsoft.Json.Linq.JObject.Parse(json);
                var ids = doc["app_ids"]?.ToObject<List<string>>() ?? doc["apps"]?.ToObject<List<string>>() ?? new List<string>();

                int matched = 0, skipped = 0;
                foreach (var id in ids)
                {
                    var row = Apps.FirstOrDefault(a => a.AppId == id);
                    if (row != null) { row.IsSelected = true; matched++; } else skipped++;
                }
                Log($"📥 Импорт: отмечено {matched}, не найдено в каталоге: {skipped}");
                if (skipped > 0)
                    MessageBox.Show($"Отмечено: {matched}\nНе найдено в текущем каталоге: {skipped}", "Импорт завершён",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка чтения файла:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Прочее ───────────────────────────────────────────────────────────────

        public void OnSourceOrderChanged()
        {
            ApplyCategorySourceHeaders();
            _ = RefreshAvailabilityAsync();
        }

        public void UpdateTimeouts()
        {
            _catalogLoader?.UpdateTimeout(AppSettings.CatalogTimeout);
            _availabilityChecker.UpdateTimeout(AppSettings.CheckTimeout);
        }

        public void CancelAvailabilityRetries() => _availabilityCts.Cancel();

        private void Log(string message)
        {
            AppLogger.Write(message);
            Application.Current?.Dispatcher.BeginInvoke(() => LogLines.Add(message));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
