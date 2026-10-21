using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Helpers;
using Ven4Tools.Services;
using Ven4Tools.Services.WindowsUpdate;

namespace Ven4Tools.ViewModels
{
    /// <summary>Строка списка «Скрытые обновления».</summary>
    public sealed class HiddenUpdateRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public WindowsUpdateItem Item { get; init; } = null!;

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        /// <summary>Тот же формат строки, что у списка найденных обновлений.</summary>
        public string DisplayText =>
            $"{Item.Title}" +
            (Item.KbArticleIds.Count > 0 ? $" (KB{string.Join(", KB", Item.KbArticleIds)})" : "") +
            $" — {SizeFormatter.BytesToMB(Item.SizeBytes)}";
    }

    /// <summary>
    /// Скрытие обновлений (IUpdate.IsHidden) и список «Скрытые» с возвратом.
    /// Повод — Windows Update раз за разом ставит старый драйвер видеокарты поверх
    /// нового: скрытый патч он больше не предлагает и сам не ставит.
    /// </summary>
    public sealed partial class WindowsUpdateViewModel
    {
        private List<WindowsUpdateItem> _foundItems = new();
        private bool _hasSearchResult;
        private bool _hiddenLoadedOnce;

        public RelayCommand HideSelectedCommand { get; private set; } = null!;
        public RelayCommand RefreshHiddenCommand { get; private set; } = null!;
        public RelayCommand UnhideSelectedCommand { get; private set; } = null!;

        private bool _isHideEnabled;
        public bool IsHideEnabled { get => _isHideEnabled; private set => SetField(ref _isHideEnabled, value); }

        private bool _isHiding;
        /// <summary>Идёт скрытие, возврат или поиск скрытых.</summary>
        public bool IsHiding
        {
            get => _isHiding;
            private set
            {
                if (!SetField(ref _isHiding, value)) return;
                CheckCommand.RaiseCanExecuteChanged();
                UpdateSelectionSummary();
                UpdateUnhideEnabled();
            }
        }

        private IReadOnlyList<HiddenUpdateRow> _hiddenUpdates = Array.Empty<HiddenUpdateRow>();
        public IReadOnlyList<HiddenUpdateRow> HiddenUpdates { get => _hiddenUpdates; private set => SetField(ref _hiddenUpdates, value); }

        private string _hiddenStatusText = "Список скрытых обновлений ещё не загружался.";
        public string HiddenStatusText { get => _hiddenStatusText; private set => SetField(ref _hiddenStatusText, value); }

        private bool _isUnhideEnabled;
        public bool IsUnhideEnabled { get => _isUnhideEnabled; private set => SetField(ref _isUnhideEnabled, value); }

        private void InitHiddenCommands()
        {
            HideSelectedCommand = RelayCommand.FromAsync(_ => RunHideSelectedAsync());
            RefreshHiddenCommand = RelayCommand.FromAsync(_ => RefreshHiddenAsync(), _ => !IsHiding);
            UnhideSelectedCommand = RelayCommand.FromAsync(_ => RunUnhideSelectedAsync());
        }

        private void OnHiddenSectionShown()
        {
            if (_hiddenLoadedOnce) return;
            // Поиск скрытых — запрос к серверам Microsoft: в параноидальном режиме сам
            // не начинается, только по кнопке.
            if (ProfileService.Current.ParanoidMode)
            {
                HiddenStatusText = "Автозагрузка списка отключена (параноидальный режим). Нажмите «Обновить список».";
                return;
            }
            _ = RefreshHiddenSafeAsync();
        }

        private async Task RefreshHiddenSafeAsync()
        {
            try { await RefreshHiddenAsync(); }
            catch (Exception ex) { AppLogger.Write(ex, "WindowsUpdateViewModel.RefreshHiddenAsync"); }
        }

        // internal — вызывается из юнит-тестов напрямую.
        internal async Task RefreshHiddenAsync()
        {
            if (IsHiding) return;
            _hiddenLoadedOnce = true;
            IsHiding = true;
            HiddenStatusText = "⏳ Поиск скрытых обновлений...";
            try
            {
                if (!await EnsureServiceRunningAsync())
                {
                    HiddenStatusText = "❌ Служба Windows Update не запущена — список скрытых недоступен.";
                    return;
                }
                var result = await _service.SearchHiddenAsync(CancellationToken.None);
                if (!result.Success)
                {
                    HiddenStatusText = $"❌ {result.ErrorMessage}";
                    return;
                }
                SetHiddenRows(result.Items);
            }
            finally
            {
                IsHiding = false;
            }
        }

        private void SetHiddenRows(IEnumerable<WindowsUpdateItem> items)
        {
            var rows = items.DistinctBy(i => i.UpdateId)
                .OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(i => new HiddenUpdateRow { Item = i })
                .ToList();
            foreach (var row in rows)
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(HiddenUpdateRow.IsChecked)) UpdateUnhideEnabled();
                };
            HiddenUpdates = rows;
            HiddenStatusText = rows.Count == 0
                ? "Скрытых обновлений нет."
                : $"Скрыто обновлений: {rows.Count}. Отметьте те, что нужно снова получать, и нажмите «Вернуть выбранные».";
            UpdateUnhideEnabled();
        }

        private void UpdateUnhideEnabled() =>
            IsUnhideEnabled = !IsHiding && HiddenUpdates.Any(r => r.IsChecked);

        /// <summary>Служба wuauserv в простое обычно остановлена — запускаем молча (как и поиск).</summary>
        private async Task<bool> EnsureServiceRunningAsync()
        {
            if (_service.IsServiceRunning()) return true;
            return await Task.Run(_service.TryStartService);
        }

        // ── Скрыть ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Текст подтверждения скрытия. Для обновлений безопасности и накопительных —
        /// отдельное предупреждение с их названиями. Чистая функция — для тестов.
        /// </summary>
        internal static (string Text, bool IsWarning) BuildHideConfirmation(IReadOnlyList<WindowsUpdateItem> selected)
        {
            var important = WindowsUpdateHideAdvisor.SelectImportant(selected);
            var sb = new StringBuilder();
            sb.Append($"Скрыть выбранные обновления ({selected.Count})?\n\n");
            sb.Append("Windows Update перестанет их предлагать и не установит сам. Вернуть их можно в разделе «Скрытые».");
            if (selected.Any(WindowsUpdateHideAdvisor.IsDriver))
                sb.Append("\n\nСкрытие драйвера помогает, когда Windows Update ставит старый драйвер (например, видеокарты) поверх нового.");
            if (important.Count > 0)
            {
                sb.Append("\n\n⚠️ Среди выбранных — обновления безопасности или накопительные:\n");
                foreach (var item in important.Take(8))
                    sb.Append($"• {item.Title}\n");
                if (important.Count > 8)
                    sb.Append($"…и ещё {important.Count - 8}\n");
                sb.Append("\nБез них компьютер не получит исправлений уязвимостей. Скрывайте такое обновление, " +
                          "только если точно знаете, что оно ломает что-то у вас, и верните его, когда выйдет исправление.");
            }
            return (sb.ToString(), important.Count > 0);
        }

        private async Task RunHideSelectedAsync()
        {
            if (IsHiding || IsInstalling || IsSearching) return;
            var selected = WindowsUpdateCategoryTreeBuilder.GetSelectedItems(Tree);
            if (selected.Count == 0) return;

            var (text, isWarning) = BuildHideConfirmation(selected);
            var answer = MessageBox.Show(text, "Скрыть обновления", MessageBoxButton.YesNo,
                isWarning ? MessageBoxImage.Warning : MessageBoxImage.Question,
                isWarning ? MessageBoxResult.No : MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes) return;

            await HideAsync(selected.Select(i => i.UpdateId).ToList());
        }

        // internal — тесты вызывают без диалога подтверждения.
        internal async Task HideAsync(IReadOnlyList<string> ids)
        {
            IsHiding = true;
            WindowsUpdateHideOutcome outcome;
            try
            {
                StatusText = "⏳ Скрытие обновлений...";
                outcome = await _service.SetHiddenAsync(ids, hidden: true, CancellationToken.None);
            }
            finally
            {
                IsHiding = false;
            }

            if (outcome.Items.Count == 0)
            {
                StatusText = $"❌ {outcome.ErrorMessage}";
                return;
            }

            var hiddenIds = outcome.Items.Where(o => o.Success).Select(o => o.UpdateId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var o in outcome.Items)
                AppLogger.Write(o.Success
                    ? $"🛡️ Windows Update: обновление скрыто — {o.Title}"
                    : $"🛡️ Windows Update: не удалось скрыть «{o.Title}» — {o.ErrorMessage}");

            // Скрытые уходят из дерева на месте и появляются в списке «Скрытые».
            var movedItems = _foundItems.Where(i => hiddenIds.Contains(i.UpdateId)).ToList();
            _foundItems = _foundItems.Where(i => !hiddenIds.Contains(i.UpdateId)).ToList();
            RebuildTreeFromFound();
            if (_hiddenLoadedOnce && movedItems.Count > 0)
                SetHiddenRows(HiddenUpdates.Select(r => r.Item).Concat(movedItems));

            var failed = outcome.Items.Where(o => !o.Success).ToList();
            StatusText = failed.Count == 0
                ? $"Скрыто обновлений: {hiddenIds.Count}. Вернуть их можно в разделе «Скрытые»."
                : $"Скрыто: {hiddenIds.Count}, не удалось: {failed.Count}. Подробности — в логе.";
            if (failed.Count > 0)
                MessageBox.Show(
                    "Не удалось скрыть:\n\n" + string.Join("\n", failed.Select(f => $"• {f.Title}: {f.ErrorMessage}")),
                    "Скрыть обновления", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void RebuildTreeFromFound()
        {
            WindowsUpdateBackgroundService.ReportSearchResult(_foundItems.Count);
            SetTree(WindowsUpdateCategoryTreeBuilder.Build(_foundItems));
            UpdateSelectionSummary();
            if (_foundItems.Count == 0)
                ShowEmptyStateInfo("Обновлений нет", "Все найденные обновления установлены или скрыты", offerDiagnostics: false);
            else
                ShowEmptyState = false;
        }

        // ── Вернуть ──────────────────────────────────────────────────────────────

        private async Task RunUnhideSelectedAsync()
        {
            if (IsHiding) return;
            var ids = HiddenUpdates.Where(r => r.IsChecked).Select(r => r.Item.UpdateId).ToList();
            if (ids.Count == 0) return;

            var answer = MessageBox.Show(
                $"Вернуть выбранные обновления ({ids.Count})?\n\n" +
                "Windows Update снова будет их предлагать и сможет установить сам по своему расписанию.",
                "Вернуть обновления", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            await UnhideAsync(ids);
        }

        // internal — тесты вызывают без диалога подтверждения.
        internal async Task UnhideAsync(IReadOnlyList<string> ids)
        {
            IsHiding = true;
            WindowsUpdateHideOutcome outcome;
            try
            {
                HiddenStatusText = "⏳ Возврат обновлений...";
                outcome = await _service.SetHiddenAsync(ids, hidden: false, CancellationToken.None);
            }
            finally
            {
                IsHiding = false;
            }

            if (outcome.Items.Count == 0)
            {
                HiddenStatusText = $"❌ {outcome.ErrorMessage}";
                return;
            }

            var shownIds = outcome.Items.Where(o => o.Success).Select(o => o.UpdateId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var o in outcome.Items)
                AppLogger.Write(o.Success
                    ? $"🛡️ Windows Update: обновление возвращено — {o.Title}"
                    : $"🛡️ Windows Update: не удалось вернуть «{o.Title}» — {o.ErrorMessage}");

            var returned = HiddenUpdates.Where(r => shownIds.Contains(r.Item.UpdateId)).Select(r => r.Item).ToList();
            SetHiddenRows(HiddenUpdates.Where(r => !shownIds.Contains(r.Item.UpdateId)).Select(r => r.Item).ToList());

            // Если список найденных уже строился — возвращённые сразу появляются в нём.
            if (_hasSearchResult && returned.Count > 0)
            {
                _foundItems = _foundItems.Concat(returned).DistinctBy(i => i.UpdateId).ToList();
                RebuildTreeFromFound();
            }

            int failed = outcome.Items.Count(o => !o.Success);
            if (failed > 0)
            {
                HiddenStatusText = $"Возвращено: {shownIds.Count}, не удалось: {failed}. Подробности — в логе.";
                MessageBox.Show(
                    "Не удалось вернуть:\n\n" + string.Join("\n",
                        outcome.Items.Where(o => !o.Success).Select(f => $"• {f.Title}: {f.ErrorMessage}")),
                    "Вернуть обновления", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else if (shownIds.Count > 0)
            {
                HiddenStatusText = $"Возвращено обновлений: {shownIds.Count}. Они снова в разделе «Обновления».";
            }
        }
    }
}
