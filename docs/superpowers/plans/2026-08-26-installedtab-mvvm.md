# InstalledTab MVVM Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Перенести логику вкладки «Установленные» (`InstalledTab`, 771 строка в 5 partial-файлах code-behind) из code-behind в `InstalledViewModel`, оставив `InstalledTab.xaml`/`.xaml.cs` тонкой обёрткой. Седьмая вкладка серии MVVM-миграции.

**Architecture:** `InstalledViewModel : INotifyPropertyChanged`, partial-класс по образцу `OfficeViewModel.*`/`CatalogViewModel.*` — `InstalledViewModel.cs` (ядро), `.Filters.cs`, `.List.cs`, `.BulkOps.cs`, `.ExportImport.cs`. Класс строки `InstalledApp` (уже `INotifyPropertyChanged`) переезжает в `Ven4Tools/ViewModels/InstalledApp.cs` без изменения тела.

**Tech Stack:** .NET 8, WPF, xUnit.

## Global Constraints

- Поведение 1:1 с оригиналом, кроме четырёх адаптаций:
  1. `this.Dispatcher.Invoke` → `System.Windows.Application.Current.Dispatcher.Invoke`.
  2. `Views.UiGuards`/`InstallationService.InstallSemaphore`/`AppUninstallService`/`WingetRunner`/`WingetArgs`/`WingetErrorMapper` вызываются из VM напрямую.
  3. `MessageBox.Show`/`Microsoft.Win32.SaveFileDialog`/`OpenFileDialog` — напрямую из VM.
  4. Статическое состояние предзагрузки (`_preloadTask`/`_cachedRawOutput`/`_preloadLock`) переезжает в `InstalledViewModel` как статические поля; `InstalledTab.StartPreload()` (публичный static, вызывается из `MainWindow.xaml.cs:99` ДО создания вкладки) делегирует в `InstalledViewModel.StartPreload()` (`internal static`). `InstalledTab.ShowUpdatesFilter()` (публичный instance, `MainWindow.xaml.cs:169`) ретранслирует в `_viewModel.ShowUpdatesFilter()`.
- **Урок вкладки Office**: любой `RangeBase.Value`/`Selector.SelectedItem`/`TextBox.Text`/`ToggleButton.IsChecked`, биндящийся на VM-свойство без публичного сеттера, ОБЯЗАН иметь явный `Mode=OneWay` — иначе гарантированный краш при активации биндинга. В этом плане `txtSearch.Text`/`cmbSort.SelectedIndex`/радиокнопки/чекбоксы биндятся на свойства с публичными сеттерами (TwoWay безопасен и корректен), `chkSelectAll.IsChecked` — на `SelectAllState` с публичным сеттером. Единственные `private set`/`internal set` свойства (`DisplayedApps`, `IsLoading` и т.п.) биндятся ТОЛЬКО на `Text`/`Visibility`/`ItemsSource`, которые OneWay по умолчанию — но перед финальным ревью всё равно обязателен грep всего XAML на этот паттерн (Task 3).
- **Гейт реентерабельности** (урок NetworkTab): `RunRefreshAsync`/`RunUpgradeAllAsync`/`RunExportAsync`/`RunImportAsync`/`RunUpdateSelectedAsync`/`RunUninstallSelectedAsync` начинаются с `if (СвойБизиФлаг) return;` первой строкой.
- `ApplyFilter()` вызывает `RecomputeStats()` + `RecomputeSelectAllState()`, но НЕ `RecomputeCanActOnSelection()` — тот же (возможно нежелательный, но не наш) пробел, что в оригинальном `ApplyFilter`/`UpdateStats`/`UpdateSelectAllState` (не вызывает `UpdateUpdateSelectedButton()`). Не «исправлять».
- `SelectAllState` сеттер НЕ перезаписывает сам себя после проставления `IsSelected` всем подходящим строкам (оригинальный `ChkSelectAll_Click` тоже этого не делает).
- `DisplayedApps` — сеттер `internal` (не `private`) для тестируемости `SelectAllState`; `DescribeWingetExitCode` — `internal static` (не `private static`) для тестируемости. `InternalsVisibleTo("Ven4Tools.Tests")` уже объявлен в `Ven4Tools/Properties/AssemblyInfo.cs`.
- Все `x:Name`, участвующие в тестах, сохраняются дословно: `btnRefresh`, `txtSearch`.
- Коммиты — на русском, без Claude/AI-атрибуции.
- Ветка `mvvm-installedtab` уже создана от `main`, спека закоммичена (`ea0872f`).

---

### Task 1: `InstalledApp` + `InstalledViewModel` (5 файлов) + юнит-тесты

**Files:**
- Create: `Ven4Tools/ViewModels/InstalledApp.cs`
- Create: `Ven4Tools/ViewModels/InstalledViewModel.cs`
- Create: `Ven4Tools/ViewModels/InstalledViewModel.Filters.cs`
- Create: `Ven4Tools/ViewModels/InstalledViewModel.List.cs`
- Create: `Ven4Tools/ViewModels/InstalledViewModel.BulkOps.cs`
- Create: `Ven4Tools/ViewModels/InstalledViewModel.ExportImport.cs`
- Test: `tests/Ven4Tools.Tests/InstalledViewModelTests.cs`

**Interfaces:**
- Consumes: `Ven4Tools.Services.AppLogger`, `Ven4Tools.Services.InstallationService.InstallSemaphore`, `Ven4Tools.Services.AppUninstallService.TryUninstallAsync`, `Ven4Tools.Services.WingetRunner` (`RunAsync`/`RunStreamingAsync`/`StripAnsi`/`IsTableSeparator`), `Ven4Tools.Services.WingetArgs` (`NonInteractiveLine`/`ModifyLine`), `Ven4Tools.Services.WingetErrorMapper.MapExitCode`, `Ven4Tools.Services.RestorePointOutcome`, `Ven4Tools.Views.UiGuards` (`WarnIfInstallBusy`/`ConfirmAndCreateRestorePointAsync`), `Ven4Tools.ViewModels.RelayCommand`/`RelayCommand.FromAsync`.
- Produces: `Ven4Tools.ViewModels.InstalledApp` (без изменений публичного API), `Ven4Tools.ViewModels.InstalledViewModel` — публичные свойства `DisplayedApps`, `IsLoading`/`IsEmpty`/`IsListVisible`, `LoadingMessage`, `IsAllFilterSelected`/`IsUnknownFilterSelected`, `OnlyUpdates`, `SearchText`, `SortIndex`, `StatsText`, `SelectAllState`, `CanUpdateSelected`/`CanUninstallSelected`; команды `RefreshCommand`/`UpgradeAllCommand`/`ExportCommand`/`ImportCommand`/`UpdateSelectedCommand`/`UninstallSelectedCommand`/`UpdateAppCommand`/`UninstallAppCommand`/`RowSelectionChangedCommand`; публичный `Task LoadAppsAsync()`; публичный `void ShowUpdatesFilter()`; `internal static void StartPreload()`.

- [ ] **Step 1: Создать `Ven4Tools/ViewModels/InstalledApp.cs`**

Полное содержимое файла:

```csharp
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Одна строка списка установленных приложений. Перенесено из
    /// Ven4Tools/Views/Tabs/InstalledTab.xaml.cs при MVVM-миграции (2026-08-26,
    /// седьмая вкладка после Debloater/History/About/Activation/Network/Office)
    /// без изменения тела.
    /// </summary>
    public class InstalledApp : INotifyPropertyChanged
    {
        public string Name      { get; set; } = "";
        public string WingetId  { get; set; } = "";
        public string Version   { get; set; } = "";

        private string _available = "";
        public string Available
        {
            get => _available;
            set { _available = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasUpdate)); }
        }

        public string Source    { get; set; } = "";

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        private bool _isProcessing;
        public bool IsProcessing
        {
            get => _isProcessing;
            set { _isProcessing = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanAct)); }
        }

        public bool HasUpdate        => !string.IsNullOrWhiteSpace(Available) && Available != "Unknown";
        public bool CanAct           => !IsProcessing;
        public bool IsVerified       => Source.Equals("winget", StringComparison.OrdinalIgnoreCase)
                                     || Source.Equals("msstore", StringComparison.OrdinalIgnoreCase);
        public bool IsUnknownSource  => string.IsNullOrWhiteSpace(Source) || Source.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

        public string SourceDisplay
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Source) || Source.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    return "❓ Неизвестный";
                if (Source.Equals("winget", StringComparison.OrdinalIgnoreCase))
                    return "✔ winget";
                if (Source.Equals("msstore", StringComparison.OrdinalIgnoreCase))
                    return "✔ Store";
                return Source;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
```

- [ ] **Step 2: Создать `Ven4Tools/ViewModels/InstalledViewModel.cs`**

Полное содержимое файла:

```csharp
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

        // Эквивалент подписки на RadioButton.Checked (не Unchecked) в оригинале —
        // фильтр пересчитывается только когда сеттер получает true.
        private void SetFilterFlag(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            if (value) ApplyFilter();
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
```

- [ ] **Step 3: Создать `Ven4Tools/ViewModels/InstalledViewModel.Filters.cs`**

Полное содержимое файла:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class InstalledViewModel
    {
        // ── Фильтрация ─────────────────────────────────────────────────────────

        private void ApplyFilter()
        {
            string search = SearchText.Trim().ToLowerInvariant();

            IEnumerable<InstalledApp> filtered = _allApps;

            if (IsUnknownFilterSelected)
                filtered = filtered.Where(a => a.IsUnknownSource);

            if (OnlyUpdates)
                filtered = filtered.Where(a => a.HasUpdate);

            if (!string.IsNullOrEmpty(search))
                filtered = filtered.Where(a =>
                    a.Name.ToLowerInvariant().Contains(search) ||
                    a.WingetId.ToLowerInvariant().Contains(search));

            // Сортировка отображаемого списка
            filtered = SortIndex switch
            {
                1 => filtered.OrderBy(a => a.Version, StringComparer.OrdinalIgnoreCase),          // по версии
                2 => filtered.OrderByDescending(a => a.HasUpdate)                                 // сначала с обновлениями
                             .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
                _ => filtered.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)              // по имени
            };

            DisplayedApps = filtered.ToList();
            RecomputeStats();
            RecomputeSelectAllState();
        }

        private void RecomputeStats()
        {
            int total   = _allApps.Count;
            int updates = _allApps.Count(a => a.HasUpdate);
            int unknown = _allApps.Count(a => a.IsUnknownSource);
            StatsText = $"Всего: {total}  |  Обновлений: {updates}  |  Неизвестных: {unknown}";
        }

        private async Task RunRefreshAsync()
        {
            if (IsRefreshing || IsUpgradingAll) return;
            try
            {
                IsRefreshing = true;
                // Сброс кэша предзагрузки — "Обновить" всегда идёт напрямую в winget
                lock (_preloadLock)
                {
                    _preloadTask = null;
                    _cachedRawOutput = null;
                }
                await LoadAppsAsync();
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка: {ex.Message}"); }
            finally { IsRefreshing = false; }
        }
    }
}
```

- [ ] **Step 4: Создать `Ven4Tools/ViewModels/InstalledViewModel.List.cs`**

Полное содержимое файла:

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class InstalledViewModel
    {
        // ── Загрузка ────────────────────────────────────────────────────────────

        public async Task LoadAppsAsync()
        {
            ShowState("loading");

            string rawOutput;
            Task? preload;
            lock (_preloadLock) { preload = _preloadTask; }
            if (preload != null)
            {
                LoadingMessage = preload.IsCompleted
                    ? "⏳ Загрузка списка приложений..."
                    : "⏳ Почти готово, дожидаемся предзагрузки...";
                // Сбой предзагрузки уже записан в журнал внутри самой задачи — здесь только
                // не даём ему всплыть повторно, кэш в этом случае просто пуст.
                try { await preload; } catch { }
                // Чтение и обнуление кэша — атомарно под блокировкой
                lock (_preloadLock)
                {
                    rawOutput = _cachedRawOutput ?? string.Empty;
                    _preloadTask = null;
                    _cachedRawOutput = null;
                }
            }
            else
            {
                LoadingMessage = "⏳ Получение списка установленных приложений...";
                var (_, output) = await WingetRunner.RunAsync(
                    $"list {WingetArgs.NonInteractiveLine}");
                rawOutput = output;
            }

            try
            {
                _allApps = ParseWingetList(rawOutput);
                ApplyFilter();
                ShowState(_allApps.Count == 0 ? "empty" : "list");
                RecomputeStats();
            }
            catch (Exception ex)
            {
                ShowState("loading");
                LoadingMessage = $"❌ Ошибка: {ex.Message}";
            }
        }

        private static List<InstalledApp> ParseWingetList(string raw)
        {
            var result = new List<InstalledApp>();
            if (string.IsNullOrWhiteSpace(raw)) return result;

            // Убрать ANSI, нормализовать переводы строк
            var lines = WingetRunner.StripAnsi(raw).Replace("\r", "").Split('\n');

            // Ищем строку-заголовок: поддерживаем английский и русский вывод winget
            int headerIdx = Array.FindIndex(lines, l =>
                (l.Contains("Name") && l.Contains("Id") && l.Contains("Version")) ||
                (l.Contains("Имя")  && l.Contains("ИД") && l.Contains("Версия")));
            if (headerIdx < 0) return result;

            string header = lines[headerIdx];
            bool isRu = !header.Contains("Name");

            string nameCol      = isRu ? "Имя"      : "Name";
            string idCol        = isRu ? "ИД"        : "Id";
            string versionCol   = isRu ? "Версия"    : "Version";
            string availableCol = isRu ? "Доступна"  : "Available";
            string sourceCol    = isRu ? "Источник"  : "Source";

            // Убрать мусор до начала заголовка "Name"/"Имя" (ANSI-артефакты, отступы)
            int namePos = header.IndexOf(nameCol, StringComparison.Ordinal);
            if (namePos < 0) return result;
            int offset = namePos;

            // Позиции колонок относительно начала первой колонки
            int colName      = 0;
            int colId        = header.IndexOf(idCol,        namePos, StringComparison.Ordinal) - offset;
            int colVersion   = header.IndexOf(versionCol,   namePos, StringComparison.Ordinal) - offset;
            int colAvailable = header.IndexOf(availableCol, namePos, StringComparison.Ordinal) - offset;
            int colSource    = header.IndexOf(sourceCol,    namePos, StringComparison.Ordinal) - offset;
            if (colId <= 0 || colVersion <= 0) return result;
            if (colAvailable < 0) colAvailable = -1;
            if (colSource    < 0) colSource    = -1;

            bool started = false;
            for (int i = headerIdx + 1; i < lines.Length; i++)
            {
                string rawLine = lines[i];
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    if (started) break; // пустая строка = начало футера
                    continue;
                }

                // Пропускаем строку-разделитель из дефисов — общим критерием
                // WingetRunner.IsTableSeparator, а не собственной копией условия
                if (WingetRunner.IsTableSeparator(rawLine)) continue;

                // Выровнять строку по offset заголовка
                string line = rawLine.Length > offset ? rawLine.Substring(offset) : rawLine;

                string name      = Extract(line, colName,    colId);
                string id        = Extract(line, colId,      colVersion);
                string version   = Extract(line, colVersion, colAvailable >= 0 ? colAvailable : line.Length);
                string available = colAvailable >= 0 ? Extract(line, colAvailable, colSource >= 0 ? colSource : line.Length) : "";
                string source    = colSource    >= 0 ? Extract(line, colSource,    line.Length) : "";

                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(id)) continue;

                started = true;
                result.Add(new InstalledApp
                {
                    Name      = name.Trim(),
                    WingetId  = id.Trim(),
                    Version   = version.Trim(),
                    Available = available.Trim(),
                    Source    = source.Trim()
                });
            }

            return result;
        }

        private static string Extract(string line, int from, int to)
        {
            if (from >= line.Length) return "";
            int end = Math.Min(to, line.Length);
            return line.Substring(from, end - from);
        }
    }
}
```

- [ ] **Step 5: Создать `Ven4Tools/ViewModels/InstalledViewModel.BulkOps.cs`**

Полное содержимое файла:

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class InstalledViewModel
    {
        // ── Обновить всё (winget upgrade --all) ─────────────────────────────────

        private async Task RunUpgradeAllAsync()
        {
            if (IsUpgradingAll) return;

            // Общий семафор с каталогом/историей/Windows Update — иначе winget
            // upgrade --all может пойти параллельно с установкой из другой вкладки
            // (конфликт msiexec, ошибка 1618).
            if (Views.UiGuards.WarnIfInstallBusy()) return;

            var res = MessageBox.Show(
                "Обновить все приложения через winget?\n\nЭто может занять продолжительное время.",
                "Обновить всё", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            // Массовое обновление — как и остальные массовые операции вкладки
            // (обновление выбранных/групповое удаление/импорт) предлагаем точку восстановления.
            var rpOutcome = await Views.UiGuards.ConfirmAndCreateRestorePointAsync(
                "Будут обновлены все приложения через winget.\n\nСоздать точку восстановления Windows перед обновлением?",
                "Ven4Tools — перед обновлением всех приложений");
            if (rpOutcome == RestorePointOutcome.Cancelled) return;

            IsUpgradingAll = true;
            AppLogger.Write("⬆ Запуск обновления всех приложений (winget upgrade --all)...");
            await InstallationService.InstallSemaphore.WaitAsync();
            try
            {
                int code = await WingetRunner.RunStreamingAsync(
                    $"upgrade --all --silent --include-unknown {WingetArgs.ModifyLine}",
                    msg => AppLogger.Write(msg));
                var upgrade = DescribeWingetExitCode(code);
                if (upgrade.Success)
                    AppLogger.Write(upgrade.Reboot
                        ? "✅ Обновление завершено. Для применения некоторых обновлений требуется перезагрузка."
                        : "✅ Обновление всех приложений завершено");
                // code == -1 — синтетический признак «winget вообще не отработал»
                else if (code != -1)
                    AppLogger.Write($"⚠ {upgrade.Reason}");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка обновления: {ex.Message}");
            }
            finally
            {
                InstallationService.InstallSemaphore.Release();
                IsUpgradingAll = false;
                // Обновляем список установленных приложений после завершения
                await LoadAppsAsync();
            }
        }

        private async Task RunUpdateSelectedAsync()
        {
            if (IsUpdatingSelected) return;
            try
            {
                if (Views.UiGuards.WarnIfInstallBusy()) return;

                var visible = DisplayedApps.Where(a => a.IsSelected && a.HasUpdate).ToList();
                if (visible.Count == 0) return;

                if (visible.Count >= 2)
                {
                    var rpOutcome = await Views.UiGuards.ConfirmAndCreateRestorePointAsync(
                        $"Будет обновлено {visible.Count} приложений.\n\nСоздать точку восстановления Windows перед обновлением?",
                        "Ven4Tools — перед массовым обновлением");
                    if (rpOutcome == RestorePointOutcome.Cancelled) return;
                }

                IsUpdatingSelected = true;
                foreach (var app in visible)
                    await UpdateAppAsync(app);
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка: {ex.Message}"); }
            finally { IsUpdatingSelected = false; }
        }

        private async Task RunUpdateAppAsync(InstalledApp? app)
        {
            try
            {
                if (app == null) return;
                if (Views.UiGuards.WarnIfInstallBusy()) return;
                await UpdateAppAsync(app);
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка: {ex.Message}"); }
        }

        private async Task RunUninstallAppAsync(InstalledApp? app)
        {
            try
            {
                if (app == null) return;
                if (Views.UiGuards.WarnIfInstallBusy()) return;

                var res = MessageBox.Show(
                    $"Удалить «{app.Name}»?",
                    "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res != MessageBoxResult.Yes) return;

                await UninstallAppAsync(app);
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка: {ex.Message}"); }
        }

        // ── Операции winget ────────────────────────────────────────────────────

        private async Task UpdateAppAsync(InstalledApp app)
        {
            app.IsProcessing = true;
            AppLogger.Write($"⬆ Обновление {app.Name}...");
            // Общий семафор с каталогом/историей/Windows Update — исключает параллельный
            // msiexec (ошибка 1618) при обновлении одновременно с установкой из другой вкладки.
            await InstallationService.InstallSemaphore.WaitAsync();
            try
            {
                // Усечённый в списке ID (winget list рисует "…" при узкой колонке) не пройдёт
                // валидацию WingetRunner.ValidateArgs — не пытаемся, чтобы не ловить неясную ошибку.
                if (string.IsNullOrWhiteSpace(app.WingetId) || app.WingetId.Contains('…'))
                {
                    AppLogger.Write($"⚠ {app.Name}: ID приложения усечён winget — обновление недоступно");
                    return;
                }

                // RunStreamingAsync: живой прогресс в лог + 15-минутный таймаут
                string args = $"upgrade --id \"{app.WingetId}\" --silent {WingetArgs.ModifyLine}";
                int code = await WingetRunner.RunStreamingAsync(args, line => AppLogger.Write($"  {line}"),
                    TimeSpan.FromMinutes(15));
                var exit = DescribeWingetExitCode(code);
                if (exit.Success)
                {
                    // Успех, в т.ч. коды «требуется перезагрузка» (3010 / 0x8A15002C)
                    app.Available = "";
                    Application.Current.Dispatcher.Invoke(() => { ApplyFilter(); RecomputeStats(); });
                    AppLogger.Write(exit.Reboot
                        ? $"✅ {app.Name} обновлён (требуется перезагрузка для завершения)"
                        : $"✅ {app.Name} обновлён");
                }
                // code == -1 (таймаут/принудительно завершён) не логируем здесь — обрабатывается отдельно
                else if (code != -1)
                {
                    AppLogger.Write($"⚠ {app.Name}: {exit.Reason}");
                }
            }
            catch (Exception ex) { AppLogger.Write($"❌ {app.Name}: {ex.Message}"); }
            finally
            {
                InstallationService.InstallSemaphore.Release();
                app.IsProcessing = false;
            }
        }

        private async Task UninstallAppAsync(InstalledApp app)
        {
            app.IsProcessing = true;
            AppLogger.Write($"🗑 Удаление {app.Name}...");
            // Общий семафор — см. комментарий в UpdateAppAsync.
            await InstallationService.InstallSemaphore.WaitAsync();
            try
            {
                bool ok = await AppUninstallService.TryUninstallAsync(app.WingetId, app.Name);
                if (ok)
                {
                    _allApps.Remove(app);
                    ApplyFilter();
                    AppLogger.Write($"✅ {app.Name} удалён");
                }
                else
                {
                    AppLogger.Write($"⚠ {app.Name}: деинсталлятор не найден");
                }
            }
            catch (Exception ex) { AppLogger.Write($"❌ {app.Name}: {ex.Message}"); }
            finally
            {
                InstallationService.InstallSemaphore.Release();
                app.IsProcessing = false;
            }
        }

        // ── Групповое удаление ────────────────────────────────────────────────

        private async Task RunUninstallSelectedAsync()
        {
            if (IsUninstallingSelected) return;
            try
            {
                if (Views.UiGuards.WarnIfInstallBusy()) return;

                var selected = DisplayedApps.Where(a => a.IsSelected && a.CanAct).ToList();
                if (selected.Count == 0) return;

                string list = string.Join("\n", selected.Take(10).Select(a => $"  • {a.Name}"));
                if (selected.Count > 10) list += $"\n  ... и ещё {selected.Count - 10}";

                var res = MessageBox.Show(
                    $"Удалить {selected.Count} приложений?\n\n{list}",
                    "Подтверждение удаления", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;

                if (selected.Count >= 2)
                {
                    var rpOutcome = await Views.UiGuards.ConfirmAndCreateRestorePointAsync(
                        $"Будет удалено {selected.Count} приложений.\n\nСоздать точку восстановления Windows перед удалением?",
                        "Ven4Tools — перед групповым удалением");
                    if (rpOutcome == RestorePointOutcome.Cancelled) return;
                }

                IsUninstallingSelected = true;

                foreach (var app in selected)
                    await UninstallAppAsync(app);
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка: {ex.Message}"); }
            finally { IsUninstallingSelected = false; }
        }
    }
}
```

- [ ] **Step 6: Создать `Ven4Tools/ViewModels/InstalledViewModel.ExportImport.cs`**

Полное содержимое файла:

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class InstalledViewModel
    {
        // ── Экспорт / Импорт ─────────────────────────────────────────────────

        private async Task RunExportAsync()
        {
            if (IsExporting) return;

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title    = "Экспорт списка приложений",
                Filter   = "Winget package list (*.winget)|*.winget|JSON (*.json)|*.json",
                FileName = $"Ven4Tools-export-{DateTime.Now:yyyy-MM-dd}"
            };
            if (dlg.ShowDialog() != true) return;

            IsExporting = true;
            AppLogger.Write($"📤 Экспорт в {System.IO.Path.GetFileName(dlg.FileName)}...");
            try
            {
                var (code, output) = await WingetRunner.RunAsync($"export -o \"{dlg.FileName}\" {WingetArgs.NonInteractiveLine}");
                // Одного File.Exists мало: SaveFileDialog разрешает выбрать уже
                // существующий файл, и при неудаче winget на диске остаётся СТАРЫЙ файл —
                // проверка проходила, и пользователь получал «✅ Экспортировано»
                // на устаревшие данные. Требуем ещё и нулевой код выхода.
                bool ok = code == 0 && System.IO.File.Exists(dlg.FileName);
                AppLogger.Write(ok ? $"✅ Экспортировано → {dlg.FileName}"
                       : $"⚠ winget: {output.Trim().Split('\n').LastOrDefault()}");
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка экспорта: {ex.Message}"); }
            finally { IsExporting = false; }
        }

        private async Task RunImportAsync()
        {
            if (IsImporting) return;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Импорт списка приложений",
                Filter = "Winget package list (*.winget)|*.winget|JSON (*.json)|*.json"
            };
            if (dlg.ShowDialog() != true) return;

            var res = MessageBox.Show(
                $"Будет запущена массовая установка всех пакетов из файла:\n\n{dlg.FileName}\n\nПродолжить?",
                "Подтверждение импорта", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;

            // Общий семафор с каталогом/историей/Windows Update — массовый winget import
            // не должен идти параллельно с другой установкой. Ранний выход по IsBusy —
            // до любых UI-мутаций.
            if (Views.UiGuards.WarnIfInstallBusy()) return;

            var rpOutcome = await Views.UiGuards.ConfirmAndCreateRestorePointAsync(
                "Импорт может установить сразу много приложений.\n\nСоздать точку восстановления Windows перед импортом?",
                "Ven4Tools — перед импортом списка");
            if (rpOutcome == RestorePointOutcome.Cancelled) return;

            IsImporting = true;
            AppLogger.Write($"📥 Импорт из {System.IO.Path.GetFileName(dlg.FileName)}...");
            AppLogger.Write("⏳ Это может занять несколько минут...");
            await InstallationService.InstallSemaphore.WaitAsync();
            try
            {
                // Успех определяется кодом выхода, а не поиском подстрок
                // «успешно»/«successfully» в выводе — проект принципиально не передаёт
                // --locale en-US, поэтому winget печатает на языке системы.
                var (code, output) = await WingetRunner.RunAsync($"import -i \"{dlg.FileName}\" {WingetArgs.ModifyLine}");
                var exit = DescribeWingetExitCode(code);

                if (exit.Success)
                    AppLogger.Write(exit.Reboot
                        ? "✅ Импорт завершён (для части пакетов требуется перезагрузка)"
                        : "✅ Импорт завершён");
                // code == -1 — синтетический признак «winget вообще не отработал»
                else if (code == -1)
                    AppLogger.Write("⚠ Импорт не выполнен: winget не отработал (причина — в логе выше)");
                else
                {
                    AppLogger.Write($"⚠ Импорт завершён с ошибками: {exit.Reason}");
                    string? lastLine = output.Trim().Split('\n')
                        .LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
                    if (!string.IsNullOrEmpty(lastLine)) AppLogger.Write($"   winget: {lastLine}");
                }

                // Обновляем список, если winget реально отработал: при частичной неудаче
                // часть пакетов всё равно установлена, и список обязан это отразить.
                if (code != -1) await LoadAppsAsync();
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка импорта: {ex.Message}"); }
            finally
            {
                InstallationService.InstallSemaphore.Release();
                IsImporting = false;
            }
        }
    }
}
```

- [ ] **Step 7: Написать `tests/Ven4Tools.Tests/InstalledViewModelTests.cs`**

Полное содержимое файла:

```csharp
using System.Collections.Generic;
using Ven4Tools.ViewModels;
using Xunit;

namespace Ven4Tools.Tests
{
    public class InstalledViewModelTests
    {
        [Fact]
        public void Конструктор_УстанавливаетДефолтныеЗначения()
        {
            var vm = new InstalledViewModel();

            Assert.True(vm.IsAllFilterSelected);
            Assert.False(vm.IsUnknownFilterSelected);
            Assert.False(vm.OnlyUpdates);
            Assert.Equal("", vm.SearchText);
            Assert.Equal(0, vm.SortIndex);
            Assert.True(vm.IsLoading);
            Assert.False(vm.IsEmpty);
            Assert.False(vm.IsListVisible);
            Assert.Equal("⏳ Получение списка установленных приложений...", vm.LoadingMessage);
            Assert.Empty(vm.DisplayedApps);
            Assert.Equal("", vm.StatsText);
            Assert.Equal((bool?)false, vm.SelectAllState);
            Assert.False(vm.CanUpdateSelected);
            Assert.False(vm.CanUninstallSelected);
        }

        [Fact]
        public void ОдиночныеКоманды_CanExecute_ИзначальноTrue()
        {
            var vm = new InstalledViewModel();

            Assert.True(vm.RefreshCommand.CanExecute(null));
            Assert.True(vm.UpgradeAllCommand.CanExecute(null));
            Assert.True(vm.ExportCommand.CanExecute(null));
            Assert.True(vm.ImportCommand.CanExecute(null));
        }

        [Fact]
        public void ГрупповыеКоманды_CanExecute_ИзначальноFalse()
        {
            // Прямо требуется существующим UI-поведением: btnUpdateSelected/btnUninstallSelected
            // стартуют статическим IsEnabled="False" в оригинальном XAML.
            var vm = new InstalledViewModel();

            Assert.False(vm.UpdateSelectedCommand.CanExecute(null));
            Assert.False(vm.UninstallSelectedCommand.CanExecute(null));
        }

        [Fact]
        public void SelectAllState_УстановкаВTrue_ВыбираетТолькоПодходящиеСтроки()
        {
            var vm = new InstalledViewModel();
            var withUpdate    = new InstalledApp { Name = "A", Available = "2.0" };
            var withoutUpdate = new InstalledApp { Name = "B", Available = "" };
            var processing    = new InstalledApp { Name = "C", Available = "2.0", IsProcessing = true };
            vm.DisplayedApps = new List<InstalledApp> { withUpdate, withoutUpdate, processing };

            vm.SelectAllState = true;

            Assert.True(withUpdate.IsSelected);
            Assert.False(withoutUpdate.IsSelected);
            Assert.False(processing.IsSelected); // !CanAct — не трогаем
        }

        [Fact]
        public void SelectAllState_УстановкаВFalse_СнимаетВыборСоВсех()
        {
            var vm = new InstalledViewModel();
            var app = new InstalledApp { Name = "A", Available = "2.0", IsSelected = true };
            vm.DisplayedApps = new List<InstalledApp> { app };

            vm.SelectAllState = false;

            Assert.False(app.IsSelected);
        }

        [Fact]
        public void RowSelectionChangedCommand_ПересчитываетCanUpdateSelected()
        {
            var vm = new InstalledViewModel();
            var app = new InstalledApp { Name = "A", Available = "2.0", IsSelected = true };
            vm.DisplayedApps = new List<InstalledApp> { app };

            vm.RowSelectionChangedCommand.Execute(null);

            Assert.True(vm.CanUpdateSelected);
            Assert.True(vm.CanUninstallSelected);
        }

        [Fact]
        public void DescribeWingetExitCode_КодНоль_УспехБезПерезагрузки()
        {
            var (success, reboot, reason) = InstalledViewModel.DescribeWingetExitCode(0);

            Assert.True(success);
            Assert.False(reboot);
            Assert.Equal("", reason);
        }

        [Fact]
        public void DescribeWingetExitCode_Код3010_УспехСПерезагрузкой()
        {
            var (success, reboot, _) = InstalledViewModel.DescribeWingetExitCode(3010);

            Assert.True(success);
            Assert.True(reboot);
        }

        [Fact]
        public void DescribeWingetExitCode_ПроизвольныйКод_НеУспех()
        {
            var (success, reboot, reason) = InstalledViewModel.DescribeWingetExitCode(1);

            Assert.False(success);
            Assert.False(reboot);
            Assert.NotEmpty(reason);
        }

        [Fact]
        public void InstalledApp_Дефолты()
        {
            var app = new InstalledApp();

            Assert.False(app.HasUpdate);
            Assert.True(app.CanAct);
            Assert.False(app.IsVerified);
            Assert.True(app.IsUnknownSource);
            Assert.Equal("❓ Неизвестный", app.SourceDisplay);
        }
    }
}
```

- [ ] **Step 8: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок, 0 предупреждений.

- [ ] **Step 9: Commit**

```bash
git add Ven4Tools/ViewModels/InstalledApp.cs Ven4Tools/ViewModels/InstalledViewModel.cs Ven4Tools/ViewModels/InstalledViewModel.Filters.cs Ven4Tools/ViewModels/InstalledViewModel.List.cs Ven4Tools/ViewModels/InstalledViewModel.BulkOps.cs Ven4Tools/ViewModels/InstalledViewModel.ExportImport.cs tests/Ven4Tools.Tests/InstalledViewModelTests.cs
git commit -m "feat(installed): InstalledViewModel (5 файлов) + юнит-тесты"
```

---

### Task 2: Переписать `InstalledTab.xaml`/`InstalledTab.xaml.cs` на тонкую обёртку

**Files:**
- Modify: `Ven4Tools/Views/Tabs/InstalledTab.xaml`
- Modify: `Ven4Tools/Views/Tabs/InstalledTab.xaml.cs`
- Delete: `Ven4Tools/Views/Tabs/InstalledTab.Filters.cs`
- Delete: `Ven4Tools/Views/Tabs/InstalledTab.List.cs`
- Delete: `Ven4Tools/Views/Tabs/InstalledTab.BulkOps.cs`
- Delete: `Ven4Tools/Views/Tabs/InstalledTab.ExportImport.cs`

**Interfaces:**
- Consumes: `Ven4Tools.ViewModels.InstalledViewModel`/`InstalledApp` (Task 1) — все публичные члены.
- Produces: `InstalledTab` с публичными членами сверх конструктора — `static void StartPreload()`, `void ShowUpdatesFilter()` (оба — внешний контракт, `MainWindow.xaml.cs:99,168-169,190`).

- [ ] **Step 1: Переписать `Ven4Tools/Views/Tabs/InstalledTab.xaml`**

Полное содержимое файла (меняются: `IsChecked`/`SelectedIndex`/`Text`/`Command`/`ItemsSource`/`Visibility` у интерактивных элементов и в `DataTemplate`; стили, статическая разметка — не трогаются):

```xml
<UserControl x:Class="Ven4Tools.Views.Tabs.InstalledTab"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="{DynamicResource ContentBackground}">

    <UserControl.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis"/>

        <!-- Стиль кнопок-фильтров -->
        <Style x:Key="FilterTabStyle" TargetType="RadioButton">
            <Setter Property="GroupName" Value="InstalledFilter"/>
            <Setter Property="Height" Value="32"/>
            <Setter Property="Padding" Value="16,0"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="RadioButton">
                        <Border x:Name="bd"
                                Background="{DynamicResource CardBackground}"
                                BorderBrush="{DynamicResource BorderBrush}"
                                BorderThickness="1" CornerRadius="6">
                            <TextBlock x:Name="lbl" Text="{TemplateBinding Content}"
                                       HorizontalAlignment="Center" VerticalAlignment="Center"
                                       FontSize="12" FontWeight="Medium"
                                       Foreground="{DynamicResource TextSecondary}"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsChecked" Value="True">
                                <Setter TargetName="bd" Property="Background" Value="{DynamicResource AccentColor}"/>
                                <Setter TargetName="bd" Property="BorderBrush" Value="{DynamicResource AccentColor}"/>
                                <Setter TargetName="lbl" Property="Foreground" Value="White"/>
                                <Setter TargetName="lbl" Property="FontWeight" Value="Bold"/>
                            </Trigger>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="bd" Property="BorderBrush" Value="{DynamicResource AccentColor}"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- Стиль строк ListView -->
        <Style TargetType="ListViewItem">
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="Padding" Value="0,3"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="BorderThickness" Value="0,0,0,1"/>
            <Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/>
            <Style.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                    <Setter Property="Background" Value="{DynamicResource BorderBrush}"/>
                </Trigger>
                <Trigger Property="IsSelected" Value="True">
                    <Setter Property="Background" Value="Transparent"/>
                </Trigger>
            </Style.Triggers>
        </Style>
    </UserControl.Resources>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- Заголовок + тулбар -->
        <Border Grid.Row="0" Padding="16,16,16,12"
                BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,0,0,1">
            <Grid>
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="12"/>
                    <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>

                <TextBlock Text="Установленные приложения" Style="{StaticResource PageTitleStyle}"/>

                <!-- Поиск + утилиты (без кнопок действий — они в фильтр-строке) -->
                <Grid Grid.Row="2">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*" MinWidth="120"/>
                        <ColumnDefinition Width="10"/>
                        <ColumnDefinition Width="Auto"/>
                    </Grid.ColumnDefinitions>

                    <TextBox x:Name="txtSearch" Grid.Column="0"
                             Height="34" Padding="10,6"
                             FontSize="13"
                             Background="{DynamicResource CardBackground}"
                             Foreground="{DynamicResource TextPrimary}"
                             BorderBrush="{DynamicResource BorderBrush}"
                             BorderThickness="1"
                             Text="{Binding SearchText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}">
                        <TextBox.Style>
                            <Style TargetType="TextBox">
                                <Style.Triggers>
                                    <Trigger Property="Text" Value="">
                                        <Setter Property="Background">
                                            <Setter.Value>
                                                <VisualBrush Stretch="None" AlignmentX="Left">
                                                    <VisualBrush.Visual>
                                                        <TextBlock Text="🔍 Поиск по названию или ID..."
                                                                   Foreground="Gray" FontSize="12"
                                                                   Margin="11,0,0,0" VerticalAlignment="Center"/>
                                                    </VisualBrush.Visual>
                                                </VisualBrush>
                                            </Setter.Value>
                                        </Setter>
                                    </Trigger>
                                </Style.Triggers>
                            </Style>
                        </TextBox.Style>
                    </TextBox>

                    <StackPanel Grid.Column="2" Orientation="Horizontal">
                        <Button x:Name="btnRefresh"
                                Content="Проверить обновления"
                                ToolTip="Обновит список установленных приложений и найдёт доступные новые версии. Ничего не устанавливает."
                                Height="34" Padding="14,0"
                                Background="{DynamicResource CardBackground}"
                                Foreground="{DynamicResource TextPrimary}"
                                FontSize="12"
                                Command="{Binding RefreshCommand}"/>
                        <!-- Обновить все приложения через winget -->
                        <Button x:Name="btnUpgradeAll"
                                Content="Обновить всё"
                                Margin="4,0,0,0" Height="34" Padding="14,0"
                                Background="{StaticResource BrandGreen}"
                                Foreground="#06130D"
                                FontWeight="Bold"
                                FontSize="12"
                                ToolTip="После подтверждения обновит через winget все приложения, для которых доступна новая версия."
                                Command="{Binding UpgradeAllCommand}"/>
                        <Button x:Name="btnExport" Content="Экспорт"
                                Margin="4,0,0,0" Height="34" Padding="10,0"
                                Background="{DynamicResource CardBackground}"
                                Foreground="{DynamicResource TextPrimary}"
                                FontSize="12"
                                ToolTip="Сохранит список установленных приложений в файл winget или JSON."
                                Command="{Binding ExportCommand}"/>
                        <Button x:Name="btnImport" Content="Импорт"
                                Margin="4,0,0,0" Height="34" Padding="10,0"
                                Background="{DynamicResource CardBackground}"
                                Foreground="{DynamicResource TextPrimary}"
                                FontSize="12"
                                ToolTip="Выберет файл winget или JSON и установит перечисленные в нём приложения."
                                Command="{Binding ImportCommand}"/>
                    </StackPanel>
                </Grid>
            </Grid>
        </Border>

        <!-- Фильтры + статистика + кнопки действий -->
        <Border Grid.Row="1" Padding="16,8"
                BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,0,0,1"
                Background="{DynamicResource SidebarBackground}">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*" MinWidth="0"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>

                <StackPanel Orientation="Horizontal" Grid.Column="0">
                    <RadioButton x:Name="rdbAll" Style="{StaticResource FilterTabStyle}"
                                 Content="Все" Margin="0,0,6,0"
                                 IsChecked="{Binding IsAllFilterSelected, Mode=TwoWay}"/>
                    <RadioButton x:Name="rdbUnknown" Style="{StaticResource FilterTabStyle}"
                                 Content="Неизвестные"
                                 IsChecked="{Binding IsUnknownFilterSelected, Mode=TwoWay}"/>

                    <!-- Разделитель -->
                    <Border Width="1" Margin="10,4" Background="{DynamicResource BorderBrush}"/>

                    <!-- Фильтр: только с обновлениями -->
                    <CheckBox x:Name="chkOnlyUpdates" Content="Только с обновлениями"
                              VerticalAlignment="Center" Margin="0,0,8,0"
                              Foreground="{DynamicResource TextSecondary}" FontSize="12"
                              IsChecked="{Binding OnlyUpdates, Mode=TwoWay}"/>

                    <!-- Сортировка -->
                    <ComboBox x:Name="cmbSort" Width="190" Height="28" VerticalAlignment="Center"
                              FontSize="12"
                              SelectedIndex="{Binding SortIndex, Mode=TwoWay}">
                        <ComboBoxItem Content="Сортировка: по имени"/>
                        <ComboBoxItem Content="Сортировка: по версии"/>
                        <ComboBoxItem Content="Сортировка: сначала с обновлениями"/>
                    </ComboBox>
                </StackPanel>

                <!-- Статистика — занимает оставшееся место, обрезается при нехватке -->
                <TextBlock x:Name="txtStats" Grid.Column="1"
                           Text="{Binding StatsText}"
                           Foreground="{DynamicResource TextSecondary}"
                           FontSize="12" VerticalAlignment="Center"
                           TextTrimming="CharacterEllipsis"
                           Margin="12,0"/>

                <!-- Кнопки действий -->
                <StackPanel Grid.Column="2" Orientation="Horizontal" VerticalAlignment="Center">
                    <Button x:Name="btnUpdateSelected"
                            Content="Обновить"
                            Height="30" Padding="12,0"
                            Background="{StaticResource BrandGreen}"
                            Foreground="#06130D"
                            FontWeight="Bold"
                            FontSize="12"
                            ToolTip="Обновит отмеченные приложения, для которых найдены новые версии."
                            Command="{Binding UpdateSelectedCommand}"/>
                    <Button x:Name="btnUninstallSelected" Content="Удалить"
                            Style="{StaticResource DangerButtonStyle}"
                            Margin="4,0,0,0"
                            Height="30" Padding="10,0"
                            FontSize="12"
                            ToolTip="После общего подтверждения удалит все отмеченные приложения по очереди."
                            Command="{Binding UninstallSelectedCommand}"/>
                </StackPanel>
            </Grid>
        </Border>

        <!-- Заголовок таблицы с чекбоксом "Выбрать все" -->
        <Border Grid.Row="2" Padding="16,6,16,6"
                Background="{DynamicResource CardBackground}"
                BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,0,0,1">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="26"/>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="95"/>
                    <ColumnDefinition Width="95"/>
                    <ColumnDefinition Width="110"/>
                    <ColumnDefinition Width="165"/>
                </Grid.ColumnDefinitions>
                <CheckBox x:Name="chkSelectAll" Grid.Column="0" VerticalAlignment="Center"
                          IsChecked="{Binding SelectAllState, Mode=TwoWay}"/>
                <TextBlock Grid.Column="1" Text="Название" FontSize="11" FontWeight="Bold"
                           Foreground="{DynamicResource TextSecondary}" VerticalAlignment="Center"/>
                <TextBlock Grid.Column="2" Text="Версия" FontSize="11" FontWeight="Bold"
                           Foreground="{DynamicResource TextSecondary}" VerticalAlignment="Center"/>
                <TextBlock Grid.Column="3" Text="Доступна" FontSize="11" FontWeight="Bold"
                           Foreground="{DynamicResource TextSecondary}" VerticalAlignment="Center"/>
                <TextBlock Grid.Column="4" Text="Источник" FontSize="11" FontWeight="Bold"
                           Foreground="{DynamicResource TextSecondary}" VerticalAlignment="Center"/>
                <TextBlock Grid.Column="5" Text="Действия" FontSize="11" FontWeight="Bold"
                           Foreground="{DynamicResource TextSecondary}" VerticalAlignment="Center"/>
            </Grid>
        </Border>

        <!-- Список приложений / индикатор загрузки -->
        <Grid Grid.Row="3">

            <!-- Loading -->
            <StackPanel x:Name="pnlLoading"
                        Visibility="{Binding IsLoading, Converter={StaticResource BoolToVis}}"
                        VerticalAlignment="Center" HorizontalAlignment="Center">
                <TextBlock x:Name="txtLoadingMsg" Text="{Binding LoadingMessage}"
                           Foreground="{DynamicResource TextSecondary}" FontSize="14"
                           HorizontalAlignment="Center"/>
                <ProgressBar IsIndeterminate="True" Height="4" Margin="0,12,0,0"
                             Width="300" Foreground="{DynamicResource AccentColor}"
                             Background="{DynamicResource BorderBrush}"/>
            </StackPanel>

            <!-- Empty state -->
            <TextBlock x:Name="pnlEmpty"
                       Visibility="{Binding IsEmpty, Converter={StaticResource BoolToVis}}"
                       Text="Приложения не найдены" FontSize="14"
                       Foreground="{DynamicResource TextSecondary}"
                       VerticalAlignment="Center" HorizontalAlignment="Center"/>

            <!-- List -->
            <ScrollViewer x:Name="listScroll"
                          Visibility="{Binding IsListVisible, Converter={StaticResource BoolToVis}}"
                          VerticalScrollBarVisibility="Auto"
                          HorizontalScrollBarVisibility="Disabled">
                <ItemsControl x:Name="lstApps" Margin="0" ItemsSource="{Binding DisplayedApps}">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <Border Padding="16,0"
                                    BorderBrush="{DynamicResource BorderBrush}"
                                    BorderThickness="0,0,0,1"
                                    Background="Transparent">
                                <Border.Style>
                                    <Style TargetType="Border">
                                        <Style.Triggers>
                                            <Trigger Property="IsMouseOver" Value="True">
                                                <Setter Property="Background" Value="{DynamicResource BorderBrush}"/>
                                            </Trigger>
                                        </Style.Triggers>
                                    </Style>
                                </Border.Style>
                                <Grid Height="38">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="26"/>
                                        <ColumnDefinition Width="*"/>
                                        <ColumnDefinition Width="95"/>
                                        <ColumnDefinition Width="95"/>
                                        <ColumnDefinition Width="110"/>
                                        <ColumnDefinition Width="165"/>
                                    </Grid.ColumnDefinitions>

                                    <!-- Чекбокс -->
                                    <CheckBox Grid.Column="0" VerticalAlignment="Center"
                                              IsChecked="{Binding IsSelected, Mode=TwoWay}"
                                              IsEnabled="{Binding CanAct}"
                                              Command="{Binding DataContext.RowSelectionChangedCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"/>

                                    <!-- Название -->
                                    <TextBlock Grid.Column="1" Text="{Binding Name}"
                                               Foreground="{DynamicResource TextPrimary}"
                                               FontSize="13" VerticalAlignment="Center"
                                               TextTrimming="CharacterEllipsis" Margin="0,0,8,0"/>

                                    <!-- Версия -->
                                    <TextBlock Grid.Column="2" Text="{Binding Version}"
                                               Foreground="{DynamicResource TextSecondary}"
                                               FontSize="12" VerticalAlignment="Center"
                                               TextTrimming="CharacterEllipsis"/>

                                    <!-- Доступна: колонка фиксированной ширины (95px) — без
                                         TextTrimming длинная версия обрезается границей колонки
                                         без многоточия, наезжая на соседнюю "Источник". -->
                                    <TextBlock Grid.Column="3"
                                               FontSize="12" VerticalAlignment="Center"
                                               FontWeight="SemiBold"
                                               TextTrimming="CharacterEllipsis">
                                        <TextBlock.Style>
                                            <Style TargetType="TextBlock">
                                                <Setter Property="Text" Value="—"/>
                                                <Setter Property="Foreground" Value="{DynamicResource TextSecondary}"/>
                                                <Style.Triggers>
                                                    <DataTrigger Binding="{Binding HasUpdate}" Value="True">
                                                        <Setter Property="Text" Value="{Binding Available}"/>
                                                        <Setter Property="Foreground" Value="#E6820E"/>
                                                    </DataTrigger>
                                                </Style.Triggers>
                                            </Style>
                                        </TextBlock.Style>
                                    </TextBlock>

                                    <!-- Источник -->
                                    <TextBlock Grid.Column="4"
                                               Text="{Binding SourceDisplay}"
                                               FontSize="12" VerticalAlignment="Center">
                                        <TextBlock.Style>
                                            <Style TargetType="TextBlock">
                                                <Setter Property="Foreground" Value="{DynamicResource TextSecondary}"/>
                                                <Style.Triggers>
                                                    <DataTrigger Binding="{Binding IsVerified}" Value="True">
                                                        <Setter Property="Foreground" Value="#4CAF50"/>
                                                    </DataTrigger>
                                                    <DataTrigger Binding="{Binding IsUnknownSource}" Value="True">
                                                        <Setter Property="Foreground" Value="#E6820E"/>
                                                    </DataTrigger>
                                                </Style.Triggers>
                                            </Style>
                                        </TextBlock.Style>
                                    </TextBlock>

                                    <!-- Действия -->
                                    <StackPanel Grid.Column="5" Orientation="Horizontal"
                                                VerticalAlignment="Center">
                                        <Button Content="Обновить"
                                                Visibility="{Binding HasUpdate, Converter={StaticResource BoolToVis}}"
                                                IsEnabled="{Binding CanAct}"
                                                Height="26" Padding="10,0" Margin="0,0,6,0"
                                                Background="{StaticResource BrandGreen}" Foreground="#06130D"
                                                FontSize="12" FontWeight="Bold"
                                                ToolTip="Обновит это приложение до доступной версии."
                                                Command="{Binding DataContext.UpdateAppCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                                CommandParameter="{Binding}"/>
                                        <Button Content="Удалить"
                                                Style="{StaticResource DangerButtonStyle}"
                                                IsEnabled="{Binding CanAct}"
                                                Height="26" Padding="10,0"
                                                ToolTip="После подтверждения удалит это приложение с компьютера."
                                                Command="{Binding DataContext.UninstallAppCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                                CommandParameter="{Binding}"/>
                                    </StackPanel>
                                </Grid>
                            </Border>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
            </ScrollViewer>
        </Grid>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Переписать `Ven4Tools/Views/Tabs/InstalledTab.xaml.cs`**

Полное содержимое файла:

```csharp
using System.Windows.Controls;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «Установленные» — тонкая обёртка над <see cref="InstalledViewModel"/>.
    /// Вся логика перенесена в ViewModel при MVVM-миграции (2026-08-26, седьмая
    /// вкладка после DebloaterTab/HistoryTab/AboutTab/ActivationTab/NetworkTab/
    /// OfficeTab). Публичные члены сверх конструктора — внешний контракт:
    /// MainWindow.xaml.cs вызывает StartPreload() до создания вкладки и
    /// ShowUpdatesFilter() на уже созданном экземпляре.
    /// </summary>
    public partial class InstalledTab : UserControl
    {
        private readonly InstalledViewModel _viewModel = new();

        public InstalledTab()
        {
            InitializeComponent();
            DataContext = _viewModel;
            Loaded += (_, _) => _ = _viewModel.LoadAppsAsync();
        }

        public static void StartPreload() => InstalledViewModel.StartPreload();

        public void ShowUpdatesFilter() => _viewModel.ShowUpdatesFilter();
    }
}
```

- [ ] **Step 3: Удалить перенесённые partial-файлы code-behind**

```bash
git rm Ven4Tools/Views/Tabs/InstalledTab.Filters.cs Ven4Tools/Views/Tabs/InstalledTab.List.cs Ven4Tools/Views/Tabs/InstalledTab.BulkOps.cs Ven4Tools/Views/Tabs/InstalledTab.ExportImport.cs
```

- [ ] **Step 4: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок, 0 предупреждений — во всех проектах, включая `Ven4Tools.ClientUITests`.

- [ ] **Step 5: Commit**

```bash
git add Ven4Tools/Views/Tabs/InstalledTab.xaml Ven4Tools/Views/Tabs/InstalledTab.xaml.cs
git commit -m "refactor(installed): InstalledTab — тонкая обёртка над InstalledViewModel"
```

---

### Task 3: Верификация — регрессия существующих тестов

**Files:**
- Не создаёт и не меняет файлы.

**Interfaces:**
- Не применимо.

- [ ] **Step 1: Полная сборка Release**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0/0.

- [ ] **Step 2: Обязательный грep XAML на `Mode=OneWay`-риск (урок Office)**

Run: `grep -nE '(Value|SelectedItem|SelectedIndex)="\{Binding [A-Za-z.]+"' Ven4Tools/Views/Tabs/InstalledTab.xaml` (без явного `Mode=` рядом)
Expected: пусто, ЛИБО (если что-то найдено) — подтвердить, что биндинг идёт на свойство с публичным сеттером. Задокументировать результат в отчёте задачи.

- [ ] **Step 3: Юнит-тесты целиком на VenchWork**

Run (на VenchWork): `dotnet test tests/Ven4Tools.Tests -c Release`
Expected: было 450/450 после OfficeTab-хотфикса (см. память `project_ven4tools_mvvm_migration_officetab_2026_08_26`) + 10 новых из `InstalledViewModelTests` = 460/460.

- [ ] **Step 4: Существующие UI-тесты на VenchWork**

Run (на VenchWork): `dotnet test Ven4Tools.ClientUITests -c Release --filter "FullyQualifiedName~Phase3RemainingTabsTests|FullyQualifiedName~KeyButtonsSmokeTests"`
Expected: `InstalledTab_ПроверитьОбновления` (реальный `winget list`, таймаут теста 60с) и все остальные тесты обоих классов — зелёные, не хуже прежнего результата (13/13 после OfficeTab).

**Если UI-прогон не укладывается в 10-15 минут** — не ждать дальше: ребутнуть VenchWork / подключить Opus 5 для диагностики / искать причину самостоятельно, начиная с `%LOCALAPPDATA%\Ven4Tools\crash_last.json` (см. `feedback_ui_test_hang_escalation` в памяти).

- [ ] **Step 5: Финальный коммит верификации**

```bash
git add -A
git status
git commit -m "test(installed): MVVM-миграция InstalledTab проверена на VenchWork" --allow-empty
```

- [ ] **Step 6: Финальное цельное ревью ветки**

Обязательный шаг перед мерджем — точечные ревью Task 1/Task 2 структурно не видят межзадачные пробелы; в предыдущих 6 вкладках подряд этот шаг находил реальные находки (в шестой — критичный краш-баг). Пакет для ревью: `scripts/review-package <merge-base main mvvm-installedtab> HEAD`. **Явно поручить ревьюеру проверить каждый `Mode=`-риск в XAML** (урок Office) — не полагаться только на его собственную инициативу.

- [ ] **Step 7: Merge + push в `main`** (без дополнительного вопроса — автономная сессия)

```bash
git checkout main
git merge --ff-only mvvm-installedtab
dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental
git push origin main
git branch -d mvvm-installedtab
```

Перед пушем — обязательно проверить все коммиты ветки на `Claude-Session`-трейлер: `git log main..mvvm-installedtab --format="%B" | grep -i claude` (должно быть пусто).

---

## После задачи

Смержено и запушено в `main`. Следующая по сложности вкладка — `DiagnosticsTab` (741 строка) — тот же процесс, новая ветка от `main`.
