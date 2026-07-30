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
