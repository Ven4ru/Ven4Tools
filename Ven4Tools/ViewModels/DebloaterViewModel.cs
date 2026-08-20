using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;
using Ven4Tools.Views.Tabs;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Вкладка «Очистка» — только UI-состояние: фильтр по категориям, отметки,
    /// прогресс и кнопки. Список твиков живёт в <see cref="DebloatCatalog"/>, а сами
    /// системные операции (Appx, реестр, службы, PowerShell) — в
    /// <see cref="DebloatTweakExecutor"/>. Перенесено из code-behind при переходе
    /// на MVVM (2026-08-21, пилот перед остальными вкладками), поведение не менялось.
    /// </summary>
    public sealed class DebloaterViewModel : INotifyPropertyChanged
    {
        private readonly List<DebloatItem> _allItems = DebloatCatalog.BuildItems();
        private CancellationTokenSource? _cts;

        public Func<Window?>? OwnerWindowProvider { get; set; }

        private string _categoryFilter = "all";
        public string CategoryFilter
        {
            get => _categoryFilter;
            set { if (SetField(ref _categoryFilter, value)) ApplyFilter(); }
        }

        private List<DebloatItem> _filteredItems = new();
        public List<DebloatItem> FilteredItems
        {
            get => _filteredItems;
            private set => SetField(ref _filteredItems, value);
        }

        private double _progressValue;
        public double ProgressValue { get => _progressValue; private set => SetField(ref _progressValue, value); }

        private Visibility _progressVisible = Visibility.Collapsed;
        public Visibility ProgressVisible { get => _progressVisible; private set => SetField(ref _progressVisible, value); }

        private string _statusText = "";
        public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }

        private bool _applyEnabled = true;
        public bool ApplyEnabled { get => _applyEnabled; private set => SetField(ref _applyEnabled, value); }

        private Visibility _cancelVisible = Visibility.Collapsed;
        public Visibility CancelVisible { get => _cancelVisible; private set => SetField(ref _cancelVisible, value); }

        private bool _cancelEnabled = true;
        public bool CancelEnabled { get => _cancelEnabled; private set => SetField(ref _cancelEnabled, value); }

        public RelayCommand SelectAllCommand { get; }
        public RelayCommand SelectNoneCommand { get; }
        public RelayCommand ApplyCommand { get; }
        public RelayCommand CancelCommand { get; }

        public DebloaterViewModel()
        {
            SelectAllCommand = new RelayCommand(_ => SelectAll());
            SelectNoneCommand = new RelayCommand(_ => SelectNone());
            ApplyCommand = RelayCommand.FromAsync(async _ => await ApplyAsync());
            CancelCommand = new RelayCommand(_ => Cancel());
            ApplyFilter();
        }

        // ── Фильтр/выбор ─────────────────────────────────────────────────────────

        /// <summary>
        /// Элементы, показанные текущим фильтром. Вынесено отдельно, потому что этот
        /// же набор нужен «Все»: она обязана отмечать ровно то, что пользователь
        /// видит на экране, а не весь список целиком.
        /// </summary>
        private List<DebloatItem> GetFilteredItems() =>
            CategoryFilter == "all"
                ? _allItems.ToList()
                : _allItems.Where(i => i.Category == CategoryFilter).ToList();

        private void ApplyFilter() => FilteredItems = GetFilteredItems();

        // Отмечаем только видимые сейчас действия. Раньше отмечались все 35 сразу:
        // пользователь, выбрав фильтр «Приложения» и нажав «Все», молча ставил галки
        // ещё и на правки реестра и на отключение служб (DiagTrack, SysMain,
        // dmwappushservice), которых на экране не было — и «Применить» их выполняло.
        private void SelectAll()
        {
            foreach (var item in GetFilteredItems()) item.IsSelected = true;
            ApplyFilter();
        }

        private void SelectNone()
        {
            foreach (var item in _allItems) item.IsSelected = false;
            ApplyFilter();
        }

        // ── Публичный доступ для снапшотов конфигурации ─────────────────────────

        /// <summary>Идентификаторы отмеченных сейчас твиков (для сохранения в снапшот).</summary>
        public IReadOnlyList<string> GetSelectedTweakIds() =>
            _allItems.Where(i => i.IsSelected).Select(i => i.Id).ToList();

        /// <summary>Отмечает в UI ровно те твики, чьи идентификаторы переданы.</summary>
        public void SetSelectedTweakIds(IReadOnlyCollection<string> ids)
        {
            foreach (var item in _allItems)
                item.IsSelected = ids.Contains(item.Id);
            ApplyFilter();
        }

        /// <summary>
        /// Применяет твики по идентификаторам тем же путём, что и обычная «Применить»
        /// (удаление Appx, реестр, службы). Используется восстановлением снапшота
        /// конфигурации. Неизвестные идентификаторы пропускаются.
        /// </summary>
        public async Task<(int Succeeded, int Total)> ApplyTweaksByIdsAsync(
            IReadOnlyCollection<string> ids,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var items = _allItems.Where(i => ids.Contains(i.Id)).ToList();
            int succeeded = 0;
            foreach (var item in items)
            {
                progress?.Report(item.Name);
                bool ok = await DebloatTweakExecutor.ApplyItemAsync(item.Category, item.Id, item.Name, ct);
                AppLogger.Write($"{(ok ? "✅" : "❌")} {item.Name} (из снапшота)");
                if (ok) succeeded++;
            }
            return (succeeded, items.Count);
        }

        // ── Apply ────────────────────────────────────────────────────────────────

        private async Task ApplyAsync()
        {
            var selected = _allItems.Where(i => i.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Ничего не выбрано.", "Debloater",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ApplyEnabled = false;
            ProgressVisible = Visibility.Visible;
            ProgressValue = 0;

            // try/finally: любое исключение в процессе (зависший PowerShell, сбой
            // сервиса и т.п.) не должно оставить кнопку и прогресс-бар навсегда
            // заблокированными — состояние восстанавливается в любом случае.
            try
            {
                var hasRisky = selected.Any(i => i.Risk is "caution" or "moderate");
                var rpOutcome = await Views.UiGuards.ConfirmAndCreateRestorePointAsync(
                    $"Будет применено {selected.Count} действий.{(hasRisky ? "\n\n⚠️ Среди них есть умеренные/опасные операции." : "")}\n\nСоздать точку восстановления Windows перед очисткой?",
                    "Ven4Tools — перед очисткой системы");
                if (rpOutcome == Views.RestorePointOutcome.Cancelled)
                {
                    StatusText = "Отменено";
                    ProgressVisible = Visibility.Collapsed;
                    return;
                }

                _cts = new CancellationTokenSource();
                CancelVisible = Visibility.Visible;
                CancelEnabled = true;
                int done = 0;
                int succeeded = 0;

                foreach (var item in selected)
                {
                    if (_cts.Token.IsCancellationRequested) break;

                    StatusText = $"⚙️ {item.Name}...";
                    ProgressValue = (double)done / selected.Count * 100;

                    bool ok = await DebloatTweakExecutor.ApplyItemAsync(item.Category, item.Id, item.Name, _cts.Token);
                    AppLogger.Write($"{(ok ? "✅" : "❌")} {item.Name}");
                    if (ok) succeeded++;
                    done++;
                }

                if (_cts.Token.IsCancellationRequested)
                {
                    StatusText = $"⏹ Остановлено: применено {succeeded} из {selected.Count}";
                }
                else
                {
                    ProgressValue = 100;
                    StatusText = $"✅ Готово: применено {succeeded} из {selected.Count}";
                }
            }
            finally
            {
                ApplyEnabled = true;
                CancelVisible = Visibility.Collapsed;
                _cts?.Dispose(); _cts = null;
            }
        }

        private void Cancel()
        {
            _cts?.Cancel();
            CancelEnabled = false;
            StatusText = "⏹ Останавливаю...";
        }

        // ── INotifyPropertyChanged ───────────────────────────────────────────────

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
