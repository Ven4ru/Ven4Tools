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
    public sealed partial class CatalogViewModel : INotifyPropertyChanged
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

        // Неуспешные установки последней пачки — с причиной из журнала сбоев и кнопкой
        // повтора. Журнал (failed_installs.json) писался и раньше, но читал его только
        // лаунчер для отчёта автору — сам пользователь своих неудач не видел.
        public ObservableCollection<FailedInstallViewModel> FailedInstalls { get; } = new();

        // Ключ — CategoryString (то же значение, что видит GroupDescription),
        // используется CategoryNameToHeaderConverter в CatalogTab.xaml.
        public Dictionary<string, CategoryHeaderViewModel> CategoryHeaders { get; } = new();

        public sealed record DiskOption(string Name, string Space);

        public RelayCommand ToggleFavoriteCommand { get; }
        public RelayCommand SuggestAlternativeCommand { get; }
        public RelayCommand OpenCardCommand { get; }
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
            RetryLoadCatalogCommand = RelayCommand.FromAsync(async _ => await LoadAsync());

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

            SavePresetCommand = RelayCommand.FromAsync(async _ => await SavePresetAsync(),
                _ => Apps.Any(a => a.IsSelected));
            ApplyPresetCommand = new RelayCommand(p => { if (p is Preset preset) ApplyPreset(preset); });
            RenamePresetCommand = RelayCommand.FromAsync(async p => { if (p is Preset preset) await RenamePresetAsync(preset); });
            UpdateAppsPresetCommand = new RelayCommand(p => { if (p is Preset preset) BeginUpdatePresetComposition(preset); });
            DeletePresetCommand = RelayCommand.FromAsync(async p => { if (p is Preset preset) await DeletePresetAsync(preset); });

            CheckUpdatesCommand = new RelayCommand(_ => SwitchToUpdatesRequested?.Invoke());

            LoadAvailableDisks();
        }

        // Отдельно от StatusText (статус загрузки каталога) — раньше это были два
        // разных TextBlock (txtLoadingStatus сверху и txtOverallStatus в панели
        // установки справа), их нельзя было схлопывать в одно свойство.
        private string _installStatusText = "Готов";
        public string InstallStatusText { get => _installStatusText; set => SetField(ref _installStatusText, value); }

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
            // Оригинальный RefreshAvailability_Click начинался с этого guard'а — без
            // него InitialLoadAvailabilityAsync и OnSourceOrderChanged могли запустить
            // проверку параллельно и затоптать друг другу _isCheckingAvailability.
            if (_isCheckingAvailability) return;
            _isCheckingAvailability = true;
            // CommandManager.RequerySuggested (см. RelayCommand) перепроверяет CanExecute
            // только на стандартные UI-события (фокус, клавиатура/мышь) — простая смена
            // приватного поля этого не вызывает. Без явного RaiseCanExecuteChanged кнопка
            // могла оставаться закэшированно disabled уже после того, как флаг снят
            // (см. AuditFixesCatalogFlowTests.Полный_Проход_Каталог — ElementNotEnabledException).
            RefreshAvailabilityCommand.RaiseCanExecuteChanged();
            try
            {
                // Без сброса кэша повторное нажатие кнопки в течение TTL (5 минут,
                // см. AvailabilityChecker.cacheDuration) просто повторяло старые
                // результаты — оригинальный RefreshAvailability_Click всегда чистил кэш.
                _availabilityChecker.ClearCache();
                Log("🔄 Запущена свежая проверка доступности...");

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
            // Сбрасываем счётчик ретраев перед первой проверкой — иначе остаток от
            // предыдущего прогона (RefreshAvailability) показал бы «Повторная
            // проверка...» уже на первой обычной проверке.
            row.RetryAttempt = 0;
            var availability = await CheckAvailabilityOnceAsync(row, sem);

            // Соответствует оригинальному CheckSingleAppAvailability: добавленные
            // пользователем приложения (произвольный winget/choco ID) — единственные,
            // для которых имеет смысл повторить проверку при первом Unavailable,
            // прежде чем показать красный статус. Каталожные приложения не ретраятся —
            // так же вело себя CheckAppAvailabilityFromCatalog в оригинале.
            int attempt = 1;
            while (availability == AppRowViewModel.RowAvailability.Unavailable && row.IsUserAdded && attempt < 3)
            {
                // Номер попытки для тултипа «⏳ Повторная проверка... (attempt/3)» —
                // выставляем до перехода в Checking, чтобы StatusTooltip уже знал счётчик.
                row.RetryAttempt = attempt;
                row.Availability = AppRowViewModel.RowAvailability.Checking;
                try { await Task.Delay(2000, _availabilityCts.Token); }
                catch (OperationCanceledException) { break; }
                attempt++;
                availability = await CheckAvailabilityOnceAsync(row, sem);
            }

            row.RetryAttempt = 0;
            row.Availability = availability;
        }

        private async Task<AppRowViewModel.RowAvailability> CheckAvailabilityOnceAsync(AppRowViewModel row, SemaphoreSlim sem)
        {
            await sem.WaitAsync();
            try
            {
                var (status, sizeMB) = await _availabilityChecker.CheckAppAvailabilityWithSize(row.App);
                if (status == AvailabilityChecker.AvailabilityStatus.Available)
                    row.AvailableSizeMB = sizeMB;
                return status switch
                {
                    AvailabilityChecker.AvailabilityStatus.Available   => AppRowViewModel.RowAvailability.Available,
                    AvailabilityChecker.AvailabilityStatus.Unavailable => AppRowViewModel.RowAvailability.Unavailable,
                    _                                                  => AppRowViewModel.RowAvailability.Unknown
                };
            }
            catch { return AppRowViewModel.RowAvailability.Unknown; }
            finally { sem.Release(); }
        }

        private async Task<bool> FetchVersionsForRowAsync(AppRowViewModel row)
        {
            if (string.IsNullOrEmpty(row.App.AlternativeId)) return false;
            var versions = await WingetVersionsService.FetchVersionsAsync(row.App.AlternativeId);
            if (versions.Count == 0) return false;
            Application.Current?.Dispatcher.Invoke(() =>
            {
                row.VersionOptions.Clear();
                row.VersionOptions.Add("Последняя");
                foreach (var v in versions) row.VersionOptions.Add(v);
                row.SelectedVersionOption = "Последняя";
                row.IsVersionComboEnabled = true;
            });
            return true;
        }

        private async Task FetchVersionsPhase2Async()
        {
            using var sem = new SemaphoreSlim(3);
            var tasks = Apps
                .Where(r => !string.IsNullOrEmpty(r.App.AlternativeId) && r.Availability != AppRowViewModel.RowAvailability.Unavailable)
                .Select(row => Task.Run(async () =>
                {
                    await sem.WaitAsync();
                    try { return await FetchVersionsForRowAsync(row); }
                    finally { sem.Release(); }
                }));
            var results = await Task.WhenAll(tasks);
            // Соответствует оригинальному AddLog($"✅ Версии загружены для {loaded}
            // приложений") из удалённого при MVVM-переносе CatalogTab.Availability.cs —
            // потерялась при рефакторинге, её ждёт AuditFixesUiTests (первичная загрузка
            // каталога считается завершённой только по этой строке).
            Log($"✅ Версии загружены для {results.Count(ok => ok)} приложений");
        }

        private async Task UpdateInstalledStatusAsync()
        {
            await _installedAppsService.RefreshAsync();
            AppLaunchResolver.InvalidateCache();
            // Первый TryResolve после InvalidateCache перестраивает весь индекс (реестр +
            // .lnk Start Menu + COM на каждый ярлык) — синхронно это фризило бы UI-поток,
            // поэтому строим индекс на фоне один раз, а сам цикл ниже — уже дешёвый lookup.
            //
            // Индекс нужен ТОЛЬКО для Play-кнопки (row.LaunchPath). Базовый статус
            // «установлено»/«есть обновление» от него не зависит, поэтому сбой построения
            // индекса не должен обрывать весь метод до цикла — иначе ни одна строка не
            // получит IsInstalled/InstalledVersion/HasUpdate. Ловим здесь и продолжаем:
            // при падении row.LaunchPath останется null у всех строк, кнопка «▶ Запустить»
            // в этот раз просто не покажется (ShowPlayButton/CanLaunch завязаны на LaunchPath).
            try
            {
                await AppLaunchResolver.EnsureIndexBuiltAsync();
            }
            catch (Exception ex)
            {
                Log($"⚠️ Не удалось построить индекс для кнопки запуска — сама проверка установленных приложений продолжится, но кнопка «▶ Запустить» в этот раз недоступна: {ex.Message}");
            }

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

        private void OpenCard(AppRowViewModel row)
        {
            var owner = OwnerWindowProvider?.Invoke();

            var cardVm = new AppCardViewModel(row, Views.UiGuards.ConfirmPackageManagerInstallAsync, SelectedInstallDrive);
            var window = new Views.AppCardWindow(cardVm) { Owner = owner };
            window.ShowDialog();
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
            if (Views.UiGuards.WarnIfInstallBusy()) return;

            InstallProgress.Clear();
            ClearFailedInstalls();
            OverallProgressPercentage = 0;
            IsInstalling = true;

            if (selected.Count >= 2)
            {
                var rpOutcome = await Views.UiGuards.ConfirmAndCreateRestorePointAsync(
                    $"Будет установлено {selected.Count} приложений.\n\nСоздать точку восстановления Windows перед установкой?",
                    "Ven4Tools — перед установкой", Log);
                if (rpOutcome == Views.RestorePointOutcome.Cancelled)
                {
                    IsInstalling = false;
                    return;
                }
            }

            _installCts = new CancellationTokenSource();
            var token = _installCts.Token;
            int completed = 0, failed = 0;
            InstallStatusText = $"⏳ Установка 0/{selected.Count}...";

            var progress = new Progress<AppInstallProgress>(p =>
            {
                var existing = InstallProgress.FirstOrDefault(x => x.AppId == p.AppId);
                if (existing != null)
                {
                    existing.Status = p.Status;
                    existing.Percentage = p.Percentage;
                    // Phase/IsIndeterminate — те же поля, что двигают цвет и режим полоски
                    // в CatalogTab (InstallPhaseToBrushConverter, ProgressBar.IsIndeterminate).
                    // Без копирования сюда защитная ветка "existing != null" тихо теряла бы
                    // смену фазы, если бы когда-нибудь появился сценарий с пересозданием
                    // AppInstallProgress для того же AppId вместо мутации одного экземпляра.
                    existing.Phase = p.Phase;
                    existing.IsIndeterminate = p.IsIndeterminate;
                }
                else InstallProgress.Add(p);

                // EffectiveProgress, а не сырой Percentage — Percentage теперь считается
                // заново в каждой фазе (0-100% скачивание, отдельно 0-100% установка), и
                // усреднение по нему "прыгало" бы назад в момент переключения фаз.
                //
                // Шорткат "всё завершено" сверяется по Phase (Done/Error), а не по
                // Percentage>=100 — после разделения на фазы Percentage достигает 100
                // ещё в середине процесса (конец фазы «Загрузка», см. «🔐 Проверка
                // SHA256...» в InstallationService), когда сама установка (elevated-
                // процесс) ещё даже не запущена. Со старым условием общая полоска
                // «Диск установки» могла ложно показать 100%, пока это же приложение
                // по факту продолжало устанавливаться — ровно тот же класс бага,
                // который и была призвана исправить замена Percentage на
                // EffectiveProgress в Average() ниже, только для сиблингового условия.
                OverallProgressPercentage = InstallProgress.All(x => x.Phase is InstallPhase.Done or InstallPhase.Error)
                    ? 100
                    : InstallProgress.Average(x => x.EffectiveProgress);
            });

            var pmConsentCache = new Dictionary<string, bool>();
            using var pmConsentLock = new SemaphoreSlim(1, 1);
            async Task<bool> ConfirmPmInstall(string pmName)
            {
                await pmConsentLock.WaitAsync();
                try
                {
                    if (pmConsentCache.TryGetValue(pmName, out bool cached)) return cached;
                    bool consented = await Views.UiGuards.ConfirmPackageManagerInstallAsync(pmName);
                    pmConsentCache[pmName] = consented;
                    return consented;
                }
                finally { pmConsentLock.Release(); }
            }

            // Момент старта пачки — граница, по которой из общего журнала сбоев
            // отбираются записи именно этой установки, а не прошлых сеансов.
            var batchStartedUtc = DateTime.UtcNow;
            var failedRows = new List<(AppRowViewModel Row, string Message)>();
            var failedRowsLock = new object();

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
                    else
                    {
                        failed++;
                        lock (failedRowsLock) failedRows.Add((row, result.Message));
                    }
                    InstallStatusText = $"⏳ Установка: {completed + failed}/{selected.Count} (✅ {completed} | ❌ {failed})";
                }
                finally { InstallationService.InstallSemaphore.Release(); }
            }, token));

            try
            {
                await Task.WhenAll(tasks);
                // При ошибках сразу указываем, где смотреть причину и как повторить —
                // иначе итог «ошибок: N» остаётся числом без объяснения.
                InstallStatusText = failed > 0
                    ? $"✅ Установка завершена. Успешно: {completed}, ошибок: {failed} — причины в блоке «Не установлено»"
                    : $"✅ Установка завершена. Успешно: {completed}, ошибок: {failed}";
                Log(InstallStatusText);
                await UpdateInstalledStatusAsync();
            }
            catch (OperationCanceledException) { InstallStatusText = "⏹️ Установка отменена"; }
            finally
            {
                IsInstalling = false;
                _installCts?.Dispose();
                _installCts = null;
                // И после обычного завершения, и после отмены: то, что не встало,
                // пользователь должен увидеть здесь же, а не только в логе.
                PublishFailedInstalls(failedRows, batchStartedUtc);
                _ = UpdateSpaceStatusAsync();
            }
        }

        // ── Неуспешные установки: список причин и повтор ────────────────────────

        public bool HasFailedInstalls => FailedInstalls.Count > 0;

        public string FailedInstallsHeader => $"⚠️ Не установлено: {FailedInstalls.Count}";

        private void ClearFailedInstalls()
        {
            if (FailedInstalls.Count == 0) return;
            FailedInstalls.Clear();
            RaiseFailedInstallsChanged();
        }

        private void RaiseFailedInstallsChanged()
        {
            OnPropertyChanged(nameof(HasFailedInstalls));
            OnPropertyChanged(nameof(FailedInstallsHeader));
        }

        /// <summary>
        /// Собирает сводку неудач пачки: список строится по фактическим результатам
        /// установки (он полный), а способ и причина подтягиваются из журнала сбоев
        /// по AppId и времени. Если записи в журнале нет (например, строгий офлайн без
        /// кэша — там журнал не пишется), показываем сообщение самого установщика.
        /// </summary>
        private void PublishFailedInstalls(
            List<(AppRowViewModel Row, string Message)> failedRows, DateTime batchStartedUtc)
        {
            FailedInstalls.Clear();

            if (failedRows.Count > 0)
            {
                var journal = InstallFailureService.ReadAll();
                foreach (var (row, message) in failedRows)
                {
                    var record = InstallFailureReport.FindLatest(journal, row.AppId, batchStartedUtc);
                    string error = !string.IsNullOrWhiteSpace(record?.Error)
                        ? record!.Error
                        : (string.IsNullOrWhiteSpace(message) ? "Причина неизвестна" : message);

                    FailedInstalls.Add(new FailedInstallViewModel(
                        row.DisplayName,
                        InstallFailureReport.MethodLabel(record?.Method),
                        error,
                        item => RetryFailedInstallAsync(row, item)));
                }
            }

            RaiseFailedInstallsChanged();
        }

        /// <summary>
        /// Повтор одной неудачной установки — тем же путём, что и обычная установка
        /// из каталога (<c>InstallationService.InstallAppAsync</c>), под тем же общим
        /// семафором. Никакой отдельной ветки установки здесь нет.
        /// </summary>
        private async Task RetryFailedInstallAsync(AppRowViewModel row, FailedInstallViewModel item)
        {
            if (Views.UiGuards.WarnIfInstallBusy()) return;

            _installService ??= new InstallationService();
            item.RetryStatus = "⏳ Повторная установка...";
            Log($"🔁 Повтор установки: {row.DisplayName}");

            var retryStartedUtc = DateTime.UtcNow;
            var progress = new Progress<AppInstallProgress>(p => item.RetryStatus = p.Status);

            await InstallationService.InstallSemaphore.WaitAsync();
            bool success;
            string message;
            try
            {
                var result = await _installService.InstallAppAsync(
                    row.App, _wingetSources, CancellationToken.None, progress, SelectedInstallDrive,
                    row.PinnedVersion, Views.UiGuards.ConfirmPackageManagerInstallAsync);
                success = result.Success;
                message = result.Message;
            }
            finally
            {
                InstallationService.InstallSemaphore.Release();
            }

            if (success)
            {
                row.JustInstalled = true;
                // Та же запись версии, что и в обычной пакетной установке (строка 990) —
                // повтор должен обновлять "версия, установленная в прошлый раз" наравне
                // с обычным путём, а не только успех с первой попытки.
                if (row.PinnedVersion != null && row.VersionOptions.Count > 1)
                    _versionTracker.TrackInstall(row.AppId, row.PinnedVersion, row.VersionOptions[1]);
                Log($"✅ Повторная установка удалась: {row.DisplayName}");
                FailedInstalls.Remove(item);
                RaiseFailedInstallsChanged();
                await UpdateInstalledStatusAsync();
                _ = UpdateSpaceStatusAsync();
                return;
            }

            // Не встало снова — показываем свежую причину вместо причины первой попытки.
            var record = InstallFailureReport.FindLatest(
                InstallFailureService.ReadAll(), row.AppId, retryStartedUtc);
            item.UpdateFailure(
                InstallFailureReport.MethodLabel(record?.Method),
                !string.IsNullOrWhiteSpace(record?.Error)
                    ? record!.Error
                    : (string.IsNullOrWhiteSpace(message) ? "Причина неизвестна" : message));
            item.RetryStatus = "❌ Повтор не удался";
            Log($"❌ Повторная установка не удалась: {row.DisplayName}");
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
                    _ = UpdateSpaceStatusAsync();
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

        private async Task UpdateSpaceStatusAsync()
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
