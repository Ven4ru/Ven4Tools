using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// ViewModel вкладки «Установленные». Логика перенесена из code-behind при
    /// MVVM-миграции (2026-08-26, седьмая вкладка после Debloater/History/About/
    /// Activation/Network/Office) без изменения поведения — см.
    /// docs/superpowers/specs/2026-08-26-installedtab-mvvm-design.md.
    /// Разбит на partial-файлы по образцу OfficeViewModel.*/CatalogViewModel.*.
    /// </summary>
    public sealed partial class InstalledViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private List<InstalledApp> _allApps = new();

        // Фоновая предзагрузка — запускается статически из MainWindow.Loaded, до
        // открытия вкладки (и до создания этой VM). Первое открытие вкладки просто
        // awaits уже идущую задачу вместо нового winget list.
        private static Task? _preloadTask;
        private static volatile string? _cachedRawOutput;

        // Синхронизация доступа к _preloadTask и _cachedRawOutput: защита от гонки
        // при одновременных вызовах (предзагрузка из MainWindow vs открытие вкладки vs «Обновить»)
        private static readonly object _preloadLock = new object();

        internal static void StartPreload()
        {
            lock (_preloadLock)
            {
                if (_preloadTask != null) return;
                _preloadTask = Task.Run(async () =>
                {
                    try
                    {
                        var (_, output) = await WingetRunner.RunAsync(
                            $"list {WingetArgs.NonInteractiveLine}");
                        _cachedRawOutput = output;
                    }
                    catch (Exception ex)
                    {
                        // Пустой вывод неотличим от «ничего не установлено»: вкладка покажет
                        // «пусто» без единого намёка на сбой winget — поэтому пишем причину.
                        AppLogger.Write(ex, "[InstalledTab] Предзагрузка списка установленных приложений не удалась");
                        _cachedRawOutput = string.Empty;
                    }
                });
            }
        }

        public void ShowUpdatesFilter()
        {
            OnlyUpdates = true;
            ApplyFilter();
        }

        // ── Список / состояние загрузки ─────────────────────────────────────────

        private IReadOnlyList<InstalledApp> _displayedApps = Array.Empty<InstalledApp>();
        public IReadOnlyList<InstalledApp> DisplayedApps
        {
            get => _displayedApps;
            internal set => SetField(ref _displayedApps, value);
        }

        private bool _isLoading = true;
        public bool IsLoading
        {
            get => _isLoading;
            private set => SetField(ref _isLoading, value);
        }

        private bool _isEmpty;
        public bool IsEmpty
        {
            get => _isEmpty;
            private set => SetField(ref _isEmpty, value);
        }

        private bool _isListVisible;
        public bool IsListVisible
        {
            get => _isListVisible;
            private set => SetField(ref _isListVisible, value);
        }

        private string _loadingMessage = "⏳ Получение списка установленных приложений...";
        public string LoadingMessage
        {
            get => _loadingMessage;
            private set => SetField(ref _loadingMessage, value);
        }

        private void ShowState(string state)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsLoading     = state == "loading";
                IsEmpty       = state == "empty";
                IsListVisible = state == "list";
            });
        }

        // ── Фильтры / сортировка ─────────────────────────────────────────────────

        private bool _isAllFilterSelected = true;
        public bool IsAllFilterSelected
        {
            get => _isAllFilterSelected;
            set => SetFilterFlag(ref _isAllFilterSelected, value);
        }

        private bool _isUnknownFilterSelected;
        public bool IsUnknownFilterSelected
        {
            get => _isUnknownFilterSelected;
            set => SetFilterFlag(ref _isUnknownFilterSelected, value);
        }

        // ApplyFilter() вызывается на ЛЮБОЕ реальное изменение флага, включая переход
        // в false. Причина: при TwoWay-биндинге радиокнопок порядок обратный порядку
        // события RadioButton.Checked — сеттер новой выбранной кнопки получает true
        // ПЕРВЫМ, а сосед получает false ВТОРЫМ. Пересчёт только на true оставлял бы
        // список отфильтрованным по старому флагу (клик «Все» после «Неизвестные»
        // не сбрасывал фильтр). Промежуточный вызов с обоими флагами true невидим:
        // всё происходит синхронно внутри одного обработчика клика, рендера между
        // записями нет, а итоговый DisplayedApps задаёт последний вызов — уже с
        // корректным состоянием.
        private void SetFilterFlag(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            ApplyFilter();
        }

        private bool _onlyUpdates;
        public bool OnlyUpdates
        {
            get => _onlyUpdates;
            set { if (SetFieldTriggering(ref _onlyUpdates, value)) ApplyFilter(); }
        }

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set { if (SetFieldTriggering(ref _searchText, value)) ApplyFilter(); }
        }

        private int _sortIndex;
        public int SortIndex
        {
            get => _sortIndex;
            set { if (SetFieldTriggering(ref _sortIndex, value)) ApplyFilter(); }
        }

        // В отличие от SetField — сообщает вызывающему, было ли реальное изменение,
        // чтобы ApplyFilter() вызывался ровно один раз на реальное изменение значения.
        private bool SetFieldTriggering<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        private string _statsText = "";
        public string StatsText
        {
            get => _statsText;
            private set => SetField(ref _statsText, value);
        }

        // ── Выбор строк ──────────────────────────────────────────────────────────

        private bool? _selectAllState = false;
        public bool? SelectAllState
        {
            get => _selectAllState;
            set
            {
                _selectAllState = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectAllState)));
                bool check = value == true;
                foreach (var app in DisplayedApps)
                    if (app.CanAct && app.HasUpdate)
                        app.IsSelected = check;
                RecomputeCanActOnSelection();
            }
        }

        private bool _canUpdateSelected;
        public bool CanUpdateSelected
        {
            get => _canUpdateSelected;
            private set { SetField(ref _canUpdateSelected, value); UpdateSelectedCommand.RaiseCanExecuteChanged(); }
        }

        private bool _canUninstallSelected;
        public bool CanUninstallSelected
        {
            get => _canUninstallSelected;
            private set { SetField(ref _canUninstallSelected, value); UninstallSelectedCommand.RaiseCanExecuteChanged(); }
        }

        public RelayCommand RowSelectionChangedCommand { get; }

        private void RecomputeCanActOnSelection()
        {
            var selected = DisplayedApps.Where(a => a.IsSelected).ToList();
            CanUpdateSelected    = selected.Any(a => a.HasUpdate);
            CanUninstallSelected = selected.Count > 0;
        }

        private void RecomputeSelectAllState()
        {
            var visible = DisplayedApps.Where(a => a.HasUpdate && a.CanAct).ToList();
            if (visible.Count == 0)
            {
                _selectAllState = false;
            }
            else
            {
                int selected = visible.Count(a => a.IsSelected);
                _selectAllState = selected == visible.Count ? true : selected == 0 ? false : (bool?)null;
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectAllState)));
        }

        // ── Busy-флаги команд ────────────────────────────────────────────────────

        private bool _isRefreshing;
        public bool IsRefreshing
        {
            get => _isRefreshing;
            private set { SetField(ref _isRefreshing, value); RefreshCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isUpgradingAll;
        public bool IsUpgradingAll
        {
            get => _isUpgradingAll;
            private set
            {
                SetField(ref _isUpgradingAll, value);
                RefreshCommand.RaiseCanExecuteChanged();
                UpgradeAllCommand.RaiseCanExecuteChanged();
            }
        }

        private bool _isExporting;
        public bool IsExporting
        {
            get => _isExporting;
            private set { SetField(ref _isExporting, value); ExportCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isImporting;
        public bool IsImporting
        {
            get => _isImporting;
            private set { SetField(ref _isImporting, value); ImportCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isUpdatingSelected;
        public bool IsUpdatingSelected
        {
            get => _isUpdatingSelected;
            private set { SetField(ref _isUpdatingSelected, value); UpdateSelectedCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isUninstallingSelected;
        public bool IsUninstallingSelected
        {
            get => _isUninstallingSelected;
            private set { SetField(ref _isUninstallingSelected, value); UninstallSelectedCommand.RaiseCanExecuteChanged(); }
        }

        // ── Команды ──────────────────────────────────────────────────────────────

        public RelayCommand RefreshCommand { get; }
        public RelayCommand UpgradeAllCommand { get; }
        public RelayCommand ExportCommand { get; }
        public RelayCommand ImportCommand { get; }
        public RelayCommand UpdateSelectedCommand { get; }
        public RelayCommand UninstallSelectedCommand { get; }
        public RelayCommand UpdateAppCommand { get; }
        public RelayCommand UninstallAppCommand { get; }

        public InstalledViewModel()
        {
            RefreshCommand            = RelayCommand.FromAsync(_ => RunRefreshAsync(),           _ => !IsRefreshing && !IsUpgradingAll);
            UpgradeAllCommand         = RelayCommand.FromAsync(_ => RunUpgradeAllAsync(),         _ => !IsUpgradingAll);
            ExportCommand             = RelayCommand.FromAsync(_ => RunExportAsync(),             _ => !IsExporting);
            ImportCommand             = RelayCommand.FromAsync(_ => RunImportAsync(),             _ => !IsImporting);
            UpdateSelectedCommand     = RelayCommand.FromAsync(_ => RunUpdateSelectedAsync(),      _ => CanUpdateSelected && !IsUpdatingSelected);
            UninstallSelectedCommand  = RelayCommand.FromAsync(_ => RunUninstallSelectedAsync(),   _ => CanUninstallSelected && !IsUninstallingSelected);
            UpdateAppCommand          = RelayCommand.FromAsync(p => RunUpdateAppAsync(p as InstalledApp));
            UninstallAppCommand       = RelayCommand.FromAsync(p => RunUninstallAppAsync(p as InstalledApp));
            RowSelectionChangedCommand = new RelayCommand(_ =>
            {
                RecomputeCanActOnSelection();
                RecomputeSelectAllState();
            });
        }

        // Расшифровка кода выхода winget/COM в единый результат: успех операции,
        // требуется ли перезагрузка и причина неуспеха. internal — тестируется напрямую.
        // Примечание: деинсталляция (TryUninstallAsync) трактует 0x8A150014 как «пакет
        // не установлен» = успех — иная семантика, поэтому сюда намеренно не сведена.
        internal static (bool Success, bool Reboot, string Reason) DescribeWingetExitCode(int code)
        {
            if (code == 0) return (true, false, "");
            if (code == 3010 || code == unchecked((int)0x8A15002C)) return (true, true, "");
            return (false, false, WingetErrorMapper.MapExitCode(code));
        }
    }
}
