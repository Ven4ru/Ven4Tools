using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
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
    //
    // Класс разбит на partial-файлы по ответственностям (тот же приём, что у
    // Services/InstallationService.*.cs и Ven4Tools.Launcher/MainWindow.*.cs):
    //   • CatalogViewModel.cs           — ядро: поля, коллекции, команды, конструктор,
    //                                     INotifyPropertyChanged, лог;
    //   • CatalogViewModel.Search.cs    — поиск, фильтры, сортировка, подсказки;
    //   • CatalogViewModel.Catalog.cs   — загрузка каталога, строки, категории,
    //                                     пользовательские приложения;
    //   • CatalogViewModel.Availability.cs — доступность, версии, установленность, карточка;
    //   • CatalogViewModel.Install.cs   — установка, отмена, прогресс, неудачные установки;
    //   • CatalogViewModel.Presets.cs   — пресеты и экспорт/импорт списка;
    //   • CatalogViewModel.Disks.cs     — диск установки и проверка свободного места.
    public sealed partial class CatalogViewModel : ViewModelBase
    {
        private readonly AppManager _appManager = new();
        private CatalogLoaderService? _catalogLoader;
        private readonly AvailabilityChecker _availabilityChecker = new();
        private readonly InstalledAppsService _installedAppsService = new();
        private readonly FavoritesService _favoritesService = new();
        private InstallationService? _installService;
        private readonly VersionTrackingService _versionTracker = new();
        private readonly IgnoredUpdatesService _ignoredUpdatesService = new();
        private readonly string[] _wingetSources = { "winget", "msstore" };
        private readonly CancellationTokenSource _availabilityCts = new();

        public ObservableCollection<AppRowViewModel> Apps { get; } = new();
        public ICollectionView AppsView { get; }
        public ObservableCollection<string> LogLines { get; } = new();
        public ObservableCollection<AppInstallProgress> InstallProgress { get; } = new();
        public ObservableCollection<SearchSuggestionViewModel> Suggestions { get; } = new();
        public ObservableCollection<Preset> Presets { get; } = new();
        public ObservableCollection<DiskOption> AvailableDisks { get; } = new();

        // Неуспешные установки последней пачки — с причиной из журнала сбоев и кнопкой
        // повтора. Журнал (failed_installs.json) писался и раньше, но читал его только
        // лаунчер для отчёта автору — сам пользователь своих неудач не видел.
        public ObservableCollection<FailedInstallViewModel> FailedInstalls { get; } = new();

        // Ключ — CategoryString (то же значение, что видит GroupDescription),
        // используется CategoryNameToHeaderConverter в CatalogTab.xaml.
        public Dictionary<string, CategoryHeaderViewModel> CategoryHeaders { get; } = new();

        public sealed record DiskOption(string Name, string Space);

        public RelayCommand ToggleFavoriteCommand { get; }
        public RelayCommand ToggleIgnoreUpdateCommand { get; }
        public RelayCommand SuggestAlternativeCommand { get; }
        public RelayCommand OpenCardCommand { get; }
        public RelayCommand RemoveUserAppCommand { get; }
        public RelayCommand HideAppCommand { get; }
        public RelayCommand InstallSelectedCommand { get; }
        public RelayCommand CancelInstallCommand { get; }
        public RelayCommand RefreshAvailabilityCommand { get; }
        public RelayCommand RefreshCatalogCommand { get; }
        public RelayCommand RetryLoadCatalogCommand { get; }
        public RelayCommand ClearAllUserAppsCommand { get; }
        public RelayCommand ClearSearchCommand { get; }
        public RelayCommand ToggleFavoritesOnlyCommand { get; }
        public RelayCommand ToggleHideInstalledCommand { get; }
        public RelayCommand ToggleSortAlphabeticallyCommand { get; }
        public RelayCommand ExportListCommand { get; }
        public RelayCommand ImportListCommand { get; }
        public RelayCommand ImportPresetByCodeCommand { get; }
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
            // Порядок категорий — фиксированный (как объявлен AppCategory), не алфавитный.
            AppsView.SortDescriptions.Add(new SortDescription(nameof(AppRowViewModel.CategorySortOrder), ListSortDirection.Ascending));
            ApplySortOrder();
            AppsView.Filter = RowFilter;

            ToggleFavoriteCommand = new RelayCommand(p =>
            {
                if (p is not AppRowViewModel row) return;
                _favoritesService.Toggle(row.AppId);
                row.IsFavorite = _favoritesService.IsFavorite(row.AppId);
                if (ShowFavoritesOnly) AppsView.Refresh();
            });

            // «Пропустить это обновление» — глушит уведомление только для той версии,
            // которая доступна сейчас (VersionOptions[1] — первая реальная версия,
            // нулевой элемент — «Последняя»). Когда выйдет более новая, сохранённая
            // версия перестанет совпадать и оранжевая метка вернётся сама.
            ToggleIgnoreUpdateCommand = new RelayCommand(p =>
            {
                if (p is not AppRowViewModel row || !row.HasUpdate) return;
                if (row.IsUpdateIgnored)
                {
                    _ignoredUpdatesService.ClearIgnore(row.AppId);
                    row.IsUpdateIgnored = false;
                    Log($"🔔 Уведомления об обновлении {row.DisplayName} снова включены");
                }
                else
                {
                    string? latest = row.VersionOptions.Count > 1 ? row.VersionOptions[1] : null;
                    if (latest == null) return;
                    _ignoredUpdatesService.Ignore(row.AppId, latest);
                    row.IsUpdateIgnored = true;
                    Log($"🔕 Обновление {row.DisplayName} до версии {latest} пропущено — уведомление появится снова при выходе новой версии");
                }
            });

            SuggestAlternativeCommand = RelayCommand.FromAsync(async p =>
            {
                if (p is AppRowViewModel row) await SuggestAlternativeAsync(row);
            });

            OpenCardCommand = new RelayCommand(p =>
            {
                if (p is AppRowViewModel row) OpenCard(row);
            });

            RemoveUserAppCommand = new RelayCommand(p =>
            {
                if (p is AppRowViewModel row) RemoveUserApp(row);
            });

            HideAppCommand = new RelayCommand(p =>
            {
                if (p is AppRowViewModel row) HideApp(row);
            });

            InstallSelectedCommand = RelayCommand.FromAsync(async _ => await InstallSelectedAsync(),
                _ => !IsInstalling && Apps.Any(a => a.IsSelected && a.IsSelectable));

            CancelInstallCommand = new RelayCommand(_ =>
            {
                if (_installCts == null) return;
                if (MessageBox.Show("Вы действительно хотите прервать установку?", "Подтверждение отмены",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _installCts.Cancel();
            }, _ => IsInstalling);

            RefreshAvailabilityCommand = RelayCommand.FromAsync(async _ => await RefreshAvailabilityAsync(),
                _ => !_isCheckingAvailability);

            RefreshCatalogCommand = RelayCommand.FromAsync(async _ => await RefreshCatalogAsync());
            // forceReload: повтор обязан заново обратиться к источникам в любом случае —
            // кнопку нажимают из-за уже показанной заглушки, и переиспользовать то, что
            // лежит в памяти, было бы для неё бессмысленно.
            RetryLoadCatalogCommand = RelayCommand.FromAsync(async _ => await LoadAsync(forceReload: true));

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
            ToggleHideInstalledCommand = new RelayCommand(_ => HideInstalled = !HideInstalled);
            ToggleSortAlphabeticallyCommand = new RelayCommand(_ => SortAlphabetically = !SortAlphabetically);

            ExportListCommand = new RelayCommand(_ => ExportList());
            ImportListCommand = new RelayCommand(_ => ImportList());
            ImportPresetByCodeCommand = new RelayCommand(_ => ImportPresetByCode());

            SavePresetCommand = RelayCommand.FromAsync(async _ => await SavePresetAsync(),
                _ => Apps.Any(a => a.IsSelected));
            ApplyPresetCommand = new RelayCommand(p => { if (p is Preset preset) ApplyPreset(preset); });
            RenamePresetCommand = RelayCommand.FromAsync(async p => { if (p is Preset preset) await RenamePresetAsync(preset); });
            UpdateAppsPresetCommand = new RelayCommand(p => { if (p is Preset preset) BeginUpdatePresetComposition(preset); });
            DeletePresetCommand = RelayCommand.FromAsync(async p => { if (p is Preset preset) await DeletePresetAsync(preset); });

            CheckUpdatesCommand = new RelayCommand(_ => SwitchToUpdatesRequested?.Invoke());

            LoadAvailableDisks();

            // Каталог — единственный долгоживущий владелец строк, поэтому обход
            // коллекции при смене темы делает он, а не сами строки (см.
            // AppRowViewModel.RefreshThemeBrushes). Отписки нет намеренно:
            // экземпляр ViewModel каталога один на весь сеанс приложения.
            ThemeService.ThemeChanged += OnThemeChanged;
        }

        /// <summary>
        /// Перечитать все кисти вкладки после смены темы. Ни одна из них не биндится
        /// на <c>DynamicResource</c> — это разовые снимки ресурса (см.
        /// <see cref="AppRowViewModel.RowBrush"/> и кисти переключателей фильтров
        /// в CatalogViewModel.Search.cs), поэтому без явного уведомления они
        /// оставались в цветах темы, активной на момент вычисления. Строки панели
        /// «Прогресс установки» — тот же случай, только цвет им даёт конвертер:
        /// см. <see cref="Models.AppInstallProgress.RefreshThemeBrushes"/>.
        /// </summary>
        private void OnThemeChanged()
        {
            OnPropertyChanged(nameof(FavoritesOnlyBrush));
            OnPropertyChanged(nameof(HideInstalledBrush));
            OnPropertyChanged(nameof(SortAlphabeticallyBrush));
            foreach (AppRowViewModel row in Apps) row.RefreshThemeBrushes();
            foreach (Models.AppInstallProgress item in InstallProgress) item.RefreshThemeBrushes();
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
    }
}
