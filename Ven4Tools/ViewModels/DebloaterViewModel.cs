using System;
using System.Collections.Generic;
using System.Linq;
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
    public sealed class DebloaterViewModel : ViewModelBase
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

        // Общий гейт занятости обоих входов в DebloatTweakExecutor: кнопки «Применить»
        // и восстановления снапшота конфигурации. internal set — тем же способом, что у
        // NetworkViewModel, состояние доступно юнит-тестам; разметка привязывается к
        // свойству только на чтение (IsEnabled).
        private bool _applyEnabled = true;
        public bool ApplyEnabled { get => _applyEnabled; internal set => SetField(ref _applyEnabled, value); }

        private Visibility _cancelVisible = Visibility.Collapsed;
        public Visibility CancelVisible { get => _cancelVisible; private set => SetField(ref _cancelVisible, value); }

        private bool _cancelEnabled = true;
        public bool CancelEnabled { get => _cancelEnabled; private set => SetField(ref _cancelEnabled, value); }

        public RelayCommand SelectAllCommand { get; }
        public RelayCommand SelectNoneCommand { get; }
        public RelayCommand ApplyCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand UndoCommand { get; }

        public DebloaterViewModel()
        {
            UndoCommand = RelayCommand.FromAsync(async p => { if (p is DebloatItem item) await UndoAsync(item); });
            RefreshUndoFlags();
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
        /// <para>Второй, отдельный вход в <see cref="DebloatTweakExecutor.ApplyItemAsync"/>,
        /// поэтому он обязан считаться с тем же гейтом <see cref="ApplyEnabled"/>, что и
        /// <c>ApplyAsync</c>: восстановление снапшота во время идущей «Применить»
        /// запускало две пачки правок реестра и служб внахлёст (кнопка «Восстановить»
        /// гейтится только флагом своей строки снапшота и про «Очистку» ничего не знает).</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">Очистка уже выполняется.</exception>
        public async Task<(int Succeeded, int Total)> ApplyTweaksByIdsAsync(
            IReadOnlyCollection<string> ids,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            // Исключение, а не тихий возврат (0, 0): вызывающий (восстановление снапшота)
            // показал бы «восстановлено твиков 0/0» как успех и скрыл бы отказ.
            if (!ApplyEnabled)
                throw new InvalidOperationException(
                    "Очистка системы уже выполняется — дождитесь её завершения.");

            ApplyEnabled = false;
            try
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
            finally
            {
                ApplyEnabled = true;
                RefreshUndoFlags();
            }
        }

        // ── Apply ────────────────────────────────────────────────────────────────

        private async Task ApplyAsync()
        {
            // Явный гейт реентерабельности, а не только IsEnabled="{Binding ApplyEnabled}"
            // в разметке: обновление привязки идёт через диспетчер и отстаёт от обработки
            // ввода, поэтому между снятием флага и реальным отключением кнопки остаётся
            // окно для повторного нажатия. Тот же гейт закрывает и вход со стороны
            // восстановления снапшота (ApplyTweaksByIdsAsync).
            if (!ApplyEnabled) return;

            var selected = _allItems.Where(i => i.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Ничего не выбрано.", "Очистка",
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
                // Единый диалог: подтверждение действия (Отмена = прервать) + предложение
                // точки восстановления. Раньше здесь было два подряд диалога с одинаковым
                // текстом «Будет применено N действий» — предупреждение о рисках свёрнуто
                // в этот же вопрос.
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
                // L10: показываем кнопку отмены на время длинной операции.
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
                RefreshUndoFlags();
            }
        }

        // ── Точечный откат ───────────────────────────────────────────────────────

        private void RefreshUndoFlags()
        {
            var recorded = new HashSet<string>(DebloatUndoService.Default.RecordedTweaks(), StringComparer.OrdinalIgnoreCase);
            foreach (var item in _allItems) item.CanUndo = recorded.Contains(item.Id);
        }

        /// <summary>
        /// Возвращает один твик к состоянию до его применения: прежние значения реестра
        /// и режим запуска службы. Остальные твики не затрагиваются.
        /// </summary>
        private async Task UndoAsync(DebloatItem item)
        {
            // Тот же гейт, что у «Применить»: откат и применение не должны идти внахлёст.
            if (!ApplyEnabled) return;

            if (MessageBox.Show(
                    $"Вернуть «{item.Name}» к состоянию до применения?\n\n" +
                    "Будут восстановлены прежние значения реестра и режим запуска службы. Остальные изменения не затрагиваются.",
                    "Ven4Tools — откат твика", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            ApplyEnabled = false;
            try
            {
                StatusText = $"↩ {item.Name}...";
                bool ok = await DebloatUndoService.Default.UndoAsync(item.Id);
                AppLogger.Write($"{(ok ? "↩" : "❌")} Откат твика: {item.Name}");
                StatusText = ok
                    ? $"↩ Возвращено: {item.Name}"
                    : $"❌ Не удалось вернуть полностью: {item.Name} — подробности в журнале";
                if (ok)
                {
                    // Возвращённый твик больше не считается применённым: следить за ним незачем.
                    DebloatAppliedJournal.Default.Forget(item.Id);
                    if (HasDrift) await CheckDriftAsync(announce: false);
                }
            }
            finally
            {
                ApplyEnabled = true;
                RefreshUndoFlags();
            }
        }

        // ── Проверка после обновления Windows ────────────────────────────────────

        // Источники вынесены полями ради тестов: настоящие читают реестр, журнал в
        // профиле пользователя и запускают PowerShell за списком пакетов.
        internal IDebloatSystemState DriftSystem { get; set; } = DebloatUndoService.Default.System;
        internal Func<IReadOnlyCollection<string>> AppliedTweaksSource { get; set; } = () =>
            DebloatAppliedJournal.Default.AppliedTweaks()
                .Concat(DebloatUndoService.Default.RecordedTweaks())
                .ToList();
        internal Func<CancellationToken, Task<IReadOnlyCollection<string>?>> InstalledAppxSource { get; set; } =
            DebloatDriftService.ListInstalledAppxAsync;

        private bool _hasDrift;
        /// <summary>Среди применённых твиков есть те, что сейчас не действуют.</summary>
        public bool HasDrift { get => _hasDrift; private set => SetField(ref _hasDrift, value); }

        private string _driftText = "";
        public string DriftText { get => _driftText; private set => SetField(ref _driftText, value); }

        private bool _driftChecked;
        private bool _checkingDrift;

        private RelayCommand? _checkDriftCommand;
        public RelayCommand CheckDriftCommand => _checkDriftCommand ??=
            RelayCommand.FromAsync(async _ => await CheckDriftAsync(announce: true), _ => !_checkingDrift);

        private RelayCommand? _reapplyDriftedCommand;
        public RelayCommand ReapplyDriftedCommand => _reapplyDriftedCommand ??=
            RelayCommand.FromAsync(async _ => await ReapplyDriftedAsync());

        /// <summary>Первая проверка при открытии вкладки; повторные открытия её не запускают.</summary>
        public async Task CheckDriftOnceAsync()
        {
            if (_driftChecked) return;
            _driftChecked = true;
            await CheckDriftAsync(announce: false);
        }

        /// <summary>
        /// Сверяет применённые твики с текущим состоянием системы и помечает те, что
        /// больше не действуют.
        /// </summary>
        /// <param name="announce">Писать ли итог в строку состояния, когда всё в порядке.</param>
        internal async Task CheckDriftAsync(bool announce)
        {
            if (_checkingDrift) return;
            _checkingDrift = true;
            CheckDriftCommand.RaiseCanExecuteChanged();
            try
            {
                var applied = new HashSet<string>(AppliedTweaksSource(), StringComparer.OrdinalIgnoreCase);
                var tracked = _allItems.Where(i => applied.Contains(i.Id)).ToList();

                // Список пакетов нужен только удалённым приложениям — без них PowerShell не запускаем.
                IReadOnlyCollection<string>? appx = tracked.Any(i => i.Category == "app")
                    ? await InstalledAppxSource(CancellationToken.None)
                    : null;

                var drifted = new HashSet<string>(
                    DebloatDriftService.FindDrifted(tracked.Select(i => (i.Category, i.Id)), DriftSystem, appx),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var item in _allItems) item.IsDrifted = drifted.Contains(item.Id);

                var names = _allItems.Where(i => i.IsDrifted).Select(i => i.Name).ToList();
                HasDrift = names.Count > 0;
                DriftText = names.Count == 0
                    ? ""
                    : $"Сейчас не действуют применённые ранее твики: {names.Count}. " +
                      string.Join(", ", names.Take(5)) + (names.Count > 5 ? " и другие" : "") +
                      ". Обычно их возвращает крупное обновление Windows.";

                if (names.Count > 0)
                    AppLogger.Write($"[Очистка] Не действуют применённые твики: {string.Join(", ", names)}");
                if (announce && names.Count == 0)
                    StatusText = tracked.Count == 0
                        ? "Проверять нечего: твики ещё не применялись"
                        : $"✅ Все применённые твики действуют: {tracked.Count}";
            }
            finally
            {
                _checkingDrift = false;
                CheckDriftCommand.RaiseCanExecuteChanged();
            }
        }

        /// <summary>
        /// Применяет заново то, что вернула система, — тем же путём, что и обычная
        /// «Применить». Если твик так и не подействовал, об этом сказано прямо: новая
        /// версия Windows могла закрыть возможность его отключить.
        /// </summary>
        private async Task ReapplyDriftedAsync()
        {
            if (!ApplyEnabled) return;

            var drifted = _allItems.Where(i => i.IsDrifted).Select(i => i.Id).ToList();
            if (drifted.Count == 0) return;

            SetSelectedTweakIds(drifted);
            await ApplyAsync();
            await CheckDriftAsync(announce: false);

            var still = _allItems.Where(i => i.IsDrifted && drifted.Contains(i.Id)).Select(i => i.Name).ToList();
            if (still.Count > 0)
                StatusText = $"⚠️ Не подействовали повторно: {string.Join(", ", still)}. " +
                             "Возможно, эта версия Windows больше не даёт их отключить.";
        }

        // L10: отмена применения твиков — прерывает цикл после текущего элемента.
        private void Cancel()
        {
            _cts?.Cancel();
            CancelEnabled = false;
            StatusText = "⏹ Останавливаю...";
        }
    }
}
