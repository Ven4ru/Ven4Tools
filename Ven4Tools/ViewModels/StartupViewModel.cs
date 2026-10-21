using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    /// <summary>Строка списка автозагрузки: запись плюс её текущее состояние.</summary>
    public sealed class StartupItemRow : ViewModelBase
    {
        public StartupEntry Entry { get; private set; }

        public StartupItemRow(StartupEntry entry) => Entry = entry;

        public string Name => Entry.Name;
        public string Command => Entry.Command;
        public string Location => Entry.Location;
        public string Publisher => Entry.Publisher;
        public bool IsSystem => Entry.IsSystem;
        public bool CanToggle => Entry.CanToggle;

        public string SourceLabel => StartupViewModel.SourceLabel(Entry.Source);

        public bool Enabled => Entry.Enabled;

        public string StateLabel => !Entry.CanToggle ? "Однократно" : Entry.Enabled ? "Включено" : "Выключено";

        public string ToggleText => Entry.Enabled ? "Выключить" : "Включить";

        private bool _busy;
        public bool IsBusy { get => _busy; set { if (SetField(ref _busy, value)) OnPropertyChanged(nameof(CanClick)); } }

        public bool CanClick => CanToggle && !IsBusy;

        /// <summary>Запись переключена — показать новое состояние.</summary>
        internal void SetEnabled(bool enabled)
        {
            Entry = Entry with { Enabled = enabled };
            OnPropertyChanged(nameof(Enabled));
            OnPropertyChanged(nameof(StateLabel));
            OnPropertyChanged(nameof(ToggleText));
        }
    }

    /// <summary>
    /// Подраздел «Автозагрузка» вкладки «Очистка»: всё, что стартует с Windows, и
    /// выключение/включение так же, как в Диспетчере задач. Записи Microsoft и
    /// компоненты Windows по умолчанию скрыты и выключаются только после отдельного
    /// предупреждения. Ничего не удаляется, службы не трогаются.
    /// </summary>
    public sealed class StartupViewModel : ViewModelBase
    {
        private List<StartupItemRow> _all = new();
        private bool _loadedOnce;

        // Источник вынесен свойством ради тестов: настоящий читает реестр и планировщик.
        internal Func<Task<IReadOnlyList<StartupEntry>>> Source { get; set; } = () => StartupItemsService.ListAsync();

        private List<StartupItemRow> _items = new();
        public List<StartupItemRow> Items { get => _items; private set => SetField(ref _items, value); }

        private bool _showSystem;
        /// <summary>Показывать записи Microsoft и компонентов Windows.</summary>
        public bool ShowSystem
        {
            get => _showSystem;
            set { if (SetField(ref _showSystem, value)) ApplyFilter(); }
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            private set { if (SetField(ref _isLoading, value)) RefreshCommand.RaiseCanExecuteChanged(); }
        }

        private string _summary = "";
        public string Summary { get => _summary; private set => SetField(ref _summary, value); }

        private string _statusText = "";
        public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }

        public RelayCommand RefreshCommand { get; }
        public RelayCommand ToggleCommand { get; }

        public StartupViewModel()
        {
            RefreshCommand = RelayCommand.FromAsync(async _ => await LoadAsync(), _ => !IsLoading);
            ToggleCommand = RelayCommand.FromAsync(async p => { if (p is StartupItemRow row) await ToggleAsync(row); });
        }

        internal static string SourceLabel(StartupSource source) => source switch
        {
            StartupSource.RegistryRun     => "Реестр",
            StartupSource.RegistryRunOnce => "Реестр, однократно",
            StartupSource.StartupFolder   => "Папка «Автозагрузка»",
            StartupSource.ScheduledTask   => "Планировщик заданий",
            _                             => source.ToString()
        };

        /// <summary>Первое чтение — при первом показе подраздела, а не при создании вкладки.</summary>
        public async Task LoadOnceAsync()
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            await LoadAsync();
        }

        internal async Task LoadAsync()
        {
            if (IsLoading) return;
            IsLoading = true;
            StatusText = "Читаю автозагрузку: реестр, папки, планировщик заданий…";
            try
            {
                var entries = await Source();
                _all = Order(entries).Select(e => new StartupItemRow(e)).ToList();
                ApplyFilter();
                StatusText = "";
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Автозагрузка] Список не получен: {ex.Message}");
                StatusText = "Список автозагрузки получить не удалось — подробности в журнале.";
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>Сторонние программы — выше системных, внутри — по имени.</summary>
        internal static IEnumerable<StartupEntry> Order(IEnumerable<StartupEntry> entries) =>
            entries.OrderBy(e => e.IsSystem)
                   .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase);

        private void ApplyFilter()
        {
            Items = _all.Where(r => ShowSystem || !r.IsSystem).ToList();
            int system = _all.Count(r => r.IsSystem);
            int enabled = _all.Count(r => r.Enabled && !r.IsSystem);
            int thirdParty = _all.Count(r => !r.IsSystem);
            Summary = $"Сторонних записей: {thirdParty}, из них включено: {enabled}. " +
                      (ShowSystem
                          ? $"Показаны и системные (Microsoft / Windows): {system}."
                          : $"Системных (Microsoft / Windows) скрыто: {system}.");
        }

        private async Task ToggleAsync(StartupItemRow row)
        {
            if (!row.CanClick) return;
            bool enable = !row.Enabled;

            if (!enable)
            {
                string question = row.Entry.IsWindowsComponent
                    ? $"«{row.Name}» — компонент Windows.\n\n" +
                      "Без понятной причины такие записи лучше не выключать: может перестать работать часть возможностей Windows. Всё равно выключить?"
                    : row.IsSystem
                    ? $"«{row.Name}» — программа Microsoft (например, OneDrive, Edge, Office).\n\n" +
                      "Её можно выключить, но тогда она не будет запускаться и обновляться в фоне вместе с Windows. Выключить?"
                    : $"Выключить «{row.Name}» из автозагрузки?\n\n" +
                      "Программа останется установленной и просто не будет запускаться вместе с Windows. Включить обратно можно здесь же.";
                if (MessageBox.Show(question, "Ven4Tools — автозагрузка", MessageBoxButton.YesNo,
                        row.Entry.IsWindowsComponent ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
            }

            row.IsBusy = true;
            try
            {
                string? error = await StartupItemsService.SetEnabledAsync(row.Entry, enable);
                if (error == null)
                {
                    row.SetEnabled(enable);
                    ApplyFilter();
                    StatusText = enable
                        ? $"✅ Включено: {row.Name}. Запустится при следующем входе в Windows."
                        : $"✅ Выключено: {row.Name}. Подействует при следующем входе в Windows.";
                }
                else
                {
                    StatusText = $"❌ {row.Name}: {error}";
                }
            }
            finally
            {
                row.IsBusy = false;
            }
        }
    }
}
