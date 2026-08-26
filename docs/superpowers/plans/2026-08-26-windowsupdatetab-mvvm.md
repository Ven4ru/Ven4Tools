# WindowsUpdateTab MVVM Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Перенести логику вкладки «Обновления Windows» (`WindowsUpdateTab`, 272 строки, единственный code-behind файл, программно построенный `TreeView` с трёхсторонними чекбоксами) в `WindowsUpdateViewModel`, оставив `WindowsUpdateTab.xaml`/`.xaml.cs` тонкой обёрткой. Десятая вкладка серии MVVM-миграции.

**Architecture:** Один файл `Ven4Tools/ViewModels/WindowsUpdateViewModel.cs : INotifyPropertyChanged` (без partial-разбиения — вкладка меньше остальных крупных). Единственная НЕ-VM правка серии: существующий сервисный файл `Ven4Tools/Services/WindowsUpdate/WindowsUpdateCategoryTreeBuilder.cs` получает `INotifyPropertyChanged` на своих двух типах узлов (`WindowsUpdateItemNode`/`WindowsUpdateCategoryNode`) — они уже архитектурно являются mutable UI-selection-моделью, статические функции над ними не меняются.

**Tech Stack:** .NET 8, WPF, xUnit.

## Global Constraints

- Поведение 1:1 с оригиналом, кроме:
  1. `WindowsUpdateService`/`ProfileService`/`AppLogger`/`MessageBox`/`EulaConfirmWindow`/`WindowsUpdateResultWindow` — из VM напрямую (устоявшийся паттерн).
  2. `OwnerWindowProvider` (`Func<Window?>?`) — для `EulaConfirmWindow`/`WindowsUpdateResultWindow`, code-behind задаёт `() => Window.GetWindow(this)`.
  3. `event Action? GoToDiagnostics;` остаётся на самом `WindowsUpdateTab` (внешний контракт, `MainWindow.xaml.cs:237`); VM получает свой `GoToDiagnostics`, code-behind ретранслирует — тот же паттерн, что `OfficeTab.GoToActivation`/`DiagnosticsTab.GoToWindowsUpdate`.
  4. `_firstRunHandled` (защита повторного `Loaded`) остаётся в code-behind — WPF-lifecycle забота, не VM-концерн.
  5. **Клик по чекбоксу категории** — не TwoWay-биндинг напрямую (риск рекурсии/непредсказуемого порядка при каскаде на детей), а `IsChecked Mode=OneWay` + `Command="{Binding DataContext.ToggleCategoryCommand, RelativeSource={RelativeSource AncestorType=TreeView}}" CommandParameter="{Binding}"`. Обработчик команды вычисляет **`newState = category.IsChecked == false`** — чистая функция состояния ДО клика, дословно воспроизводящая итог оригинального `categoryCheck.IsChecked == true` ПОСЛЕ уже отработавшего встроенного цикла `ToggleButton` для `IsThreeState="True"` (`true→null→false→true`). Итог: снятая категория (`false`) при клике полностью выбирается; любая другая (`true` или частично `null`) — полностью снимается. Разбор цикла — в спеке, раздел «Разбор клика по чекбоксу категории».
  6. Чекбокс патча (лист дерева) — обычный `IsChecked="{Binding IsChecked, Mode=TwoWay}"` на `WindowsUpdateItemNode.IsChecked` (`bool`, публичный set + INPC) — безопасен, реальный ввод, каскада вниз нет.
  7. Пересчёт tri-state категории после клика по патчу (оригинал: `RecalculateCategoryState` + `UpdateSelectionSummary()` + ручная синхронизация чекбокса категории без полной перерисовки) — переносится через подписку VM на `PropertyChanged` каждого `WindowsUpdateItemNode.IsChecked` в момент замены дерева (`internal void SetTree(...)`, вызывается из успешной ветки `RunSearchAsync`). Старые подписки не отписываются явно — дерево заменяется целиком при каждом поиске, как и в оригинале.
- **Гейт реентерабельности** (урок NetworkTab): `CheckCommand.CanExecute: !IsSearching && !IsInstalling` (калька `btnCheck.IsEnabled=false` и при поиске, и при установке). `RunInstallAsync` начинается с `if (IsInstalling) return;` — реальной гонки здесь нет (модальный диалог подтверждения уже сериализует доступ до этой строки), гейт добавлен для консистентности с остальной серией, не расширяет функциональный объём.
- `IsInstallEnabled` (bool, `private set`) — НЕ через `CanExecute` команды, обычное bound-свойство на `Button.IsEnabled` (OneWay по умолчанию, риска нет) — точная калька оригинального `btnInstall.IsEnabled = selectedIds.Count > 0 && !WindowsUpdateService.IsBusy`, пересчитывается `UpdateSelectionSummary()` ровно в тех местах, где это делал оригинал, плюс явно гасится в `false` в момент старта установки (калька прямого `btnInstall.IsEnabled = false;`).
- Все `x:Name`, участвующие в UI-тестах, сохраняются дословно: `btnWindowsUpdateTab` (в MainWindow), `btnCheck`, `txtStatus`, `btnOpenDiagnostics`.
- Коммиты — на русском, без Claude/AI-атрибуции.
- Ветка `mvvm-windowsupdatetab` уже создана от `main`, спека закоммичена (`86f779d`).

---

### Task 1: `WindowsUpdateCategoryTreeBuilder` (INPC) + `WindowsUpdateViewModel` + юнит-тесты

**Files:**
- Modify: `Ven4Tools/Services/WindowsUpdate/WindowsUpdateCategoryTreeBuilder.cs`
- Create: `Ven4Tools/ViewModels/WindowsUpdateViewModel.cs`
- Test: `tests/Ven4Tools.Tests/WindowsUpdateViewModelTests.cs`

**Interfaces:**
- Consumes: `Ven4Tools.Services.WindowsUpdate.WindowsUpdateService` (`IsServiceRunning()`/`TryStartService()`/`SearchAsync(CancellationToken)`/`InstallSelectedAsync(IReadOnlyList<string>, IProgress<WindowsUpdateProgress>, CancellationToken)`/`static bool IsBusy`), `WindowsUpdateItem`/`WindowsUpdateSearchResult`/`WindowsUpdateProgress`/`WindowsUpdateInstallOutcome` (не трогаем), `Ven4Tools.Services.ProfileService`/`AppLogger`, `Ven4Tools.Views.EulaConfirmWindow`/`WindowsUpdateResultWindow`, `Ven4Tools.Helpers.SizeFormatter.BytesToMB`, `Ven4Tools.ViewModels.RelayCommand`/`RelayCommand.FromAsync`.
- Produces: `WindowsUpdateItemNode`/`WindowsUpdateCategoryNode` теперь `INotifyPropertyChanged` + новые вычисляемые `DisplayText`/`HeaderText`; `Ven4Tools.ViewModels.WindowsUpdateViewModel` — публичные свойства `LastCheckedText`/`StatusText`/`Tree`/`ShowEmptyState`/`EmptyStateTitle`/`EmptyStateSubtitle`/`ShowOpenDiagnosticsButton`/`SelectionSummaryText`/`IsInstallEnabled`/`IsSearching`/`IsInstalling`; команды `CheckCommand`/`InstallCommand`/`ToggleCategoryCommand`/`OpenDiagnosticsCommand`; делегат `OwnerWindowProvider`; событие `GoToDiagnostics`; публичный `Task InitializeAsync()`; `internal void SetTree(IReadOnlyList<WindowsUpdateCategoryNode>)` (тестируемый seam).

- [ ] **Step 1: Изменить `Ven4Tools/Services/WindowsUpdate/WindowsUpdateCategoryTreeBuilder.cs`**

Полное содержимое файла (изменения — `INotifyPropertyChanged` на обоих типах узлов + два вычисляемых свойства `DisplayText`/`HeaderText`; статический класс `WindowsUpdateCategoryTreeBuilder` со всеми методами — БЕЗ изменений):

```csharp
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Ven4Tools.Helpers;

namespace Ven4Tools.Services.WindowsUpdate
{
    public sealed class WindowsUpdateItemNode : INotifyPropertyChanged
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

        /// <summary>Текст строки патча для UI — раньше собирался в WindowsUpdateTab.RenderTree().</summary>
        public string DisplayText =>
            $"{Item.Title}" +
            (Item.KbArticleIds.Count > 0 ? $" (KB{string.Join(", KB", Item.KbArticleIds)})" : "") +
            $" — {SizeFormatter.BytesToMB(Item.SizeBytes)}";
    }

    public sealed class WindowsUpdateCategoryNode : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; init; } = "";
        public List<WindowsUpdateItemNode> Items { get; init; } = new();

        // null = частично выбрано (tri-state), true = все выбраны, false = ни одного.
        private bool? _isChecked = false;
        public bool? IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        /// <summary>Текст заголовка категории для UI — раньше собирался в WindowsUpdateTab.RenderTree().</summary>
        public string HeaderText => $"{Name} ({Items.Count})";
    }

    /// <summary>
    /// Группирует патчи по категориям (IUpdate.Categories может содержать несколько —
    /// патч попадает в дерево под каждой своей категорией, это ожидаемое поведение
    /// Windows Update: например, один патч может быть и "Security Updates", и "Critical Updates").
    /// Категория без имени (в API это возможно для мусорных/служебных категорий) — в "Другое".
    /// </summary>
    public static class WindowsUpdateCategoryTreeBuilder
    {
        private const string Uncategorized = "Другое";

        public static IReadOnlyList<WindowsUpdateCategoryNode> Build(IReadOnlyList<WindowsUpdateItem> items)
        {
            var byCategory = new Dictionary<string, WindowsUpdateCategoryNode>();

            foreach (var item in items)
            {
                var categoryNames = item.CategoryNames.Count > 0
                    ? item.CategoryNames
                    : new[] { Uncategorized };

                foreach (var categoryName in categoryNames)
                {
                    var name = string.IsNullOrWhiteSpace(categoryName) ? Uncategorized : categoryName;
                    if (!byCategory.TryGetValue(name, out var node))
                    {
                        node = new WindowsUpdateCategoryNode { Name = name };
                        byCategory[name] = node;
                    }
                    node.Items.Add(new WindowsUpdateItemNode { Item = item, IsChecked = false });
                }
            }

            return byCategory.Values.OrderBy(n => n.Name).ToList();
        }

        /// <summary>Вызывать после того, как пользователь щёлкнул чекбокс отдельного патча.</summary>
        public static void RecalculateCategoryState(WindowsUpdateCategoryNode category)
        {
            if (category.Items.Count == 0) { category.IsChecked = false; return; }

            bool allChecked = category.Items.All(i => i.IsChecked);
            bool noneChecked = category.Items.All(i => !i.IsChecked);

            category.IsChecked = allChecked ? true : noneChecked ? false : (bool?)null;
        }

        /// <summary>Вызывать после того, как пользователь щёлкнул чекбокс категории.</summary>
        public static void ApplyCategoryCheck(WindowsUpdateCategoryNode category, bool isChecked)
        {
            foreach (var item in category.Items)
                item.IsChecked = isChecked;
            category.IsChecked = isChecked;
        }

        public static IReadOnlyList<string> GetSelectedUpdateIds(IReadOnlyList<WindowsUpdateCategoryNode> tree) =>
            tree.SelectMany(c => c.Items)
                .Where(i => i.IsChecked)
                .Select(i => i.Item.UpdateId)
                .Distinct()
                .ToList();

        public static long GetSelectedTotalSizeBytes(IReadOnlyList<WindowsUpdateCategoryNode> tree) =>
            tree.SelectMany(c => c.Items)
                .Where(i => i.IsChecked)
                .Select(i => i.Item)
                .DistinctBy(i => i.UpdateId)
                .Sum(i => i.SizeBytes);

        /// <summary>
        /// Патчи среди выбранных, у которых есть непринятый EULA — их текст нужно
        /// показать в диалоге подтверждения перед стартом установки. Раньше жил в
        /// WindowsUpdateErrorMapper (маппер кодов ошибок) — работает над тем же
        /// деревом, что и GetSelectedUpdateIds/GetSelectedTotalSizeBytes выше, к
        /// расшифровке кодов ошибок отношения не имеет.
        /// </summary>
        public static IReadOnlyList<WindowsUpdateItem> GetItemsNeedingEula(
            IReadOnlyList<WindowsUpdateCategoryNode> tree)
        {
            return tree
                .SelectMany(c => c.Items)
                .Where(i => i.IsChecked)
                .Select(i => i.Item)
                .Where(item => !item.EulaAccepted && !string.IsNullOrWhiteSpace(item.EulaText))
                .DistinctBy(item => item.UpdateId)
                .ToList();
        }
    }
}
```

- [ ] **Step 2: Создать `Ven4Tools/ViewModels/WindowsUpdateViewModel.cs`**

```csharp
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using Ven4Tools.Helpers;
using Ven4Tools.Services;
using Ven4Tools.Services.WindowsUpdate;
using Ven4Tools.Views;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// ViewModel вкладки «Обновления Windows». Логика перенесена из code-behind при
    /// MVVM-миграции (2026-08-26, десятая вкладка после Debloater/History/About/
    /// Activation/Network/Office/Installed/Diagnostics/System) без изменения
    /// поведения — см. docs/superpowers/specs/2026-08-26-windowsupdatetab-mvvm-design.md.
    /// </summary>
    public sealed class WindowsUpdateViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Окно-владелец для EulaConfirmWindow/WindowsUpdateResultWindow.</summary>
        public Func<Window?>? OwnerWindowProvider { get; set; }

        /// <summary>
        /// Запрос на переход к вкладке «Диагностика». Сама вкладка обновлений про
        /// навигацию не знает — переключением занимается MainWindow, как это уже
        /// сделано у OfficeTab.GoToActivation/DiagnosticsTab.GoToWindowsUpdate.
        /// </summary>
        public event Action? GoToDiagnostics;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private readonly WindowsUpdateService _service = new();
        private CancellationTokenSource? _searchCts;

        public RelayCommand CheckCommand { get; }
        public RelayCommand InstallCommand { get; }
        public RelayCommand ToggleCategoryCommand { get; }
        public RelayCommand OpenDiagnosticsCommand { get; }

        public WindowsUpdateViewModel()
        {
            CheckCommand = RelayCommand.FromAsync(_ => RunSearchAsync(), _ => !IsSearching && !IsInstalling);
            InstallCommand = RelayCommand.FromAsync(_ => RunInstallAsync());
            ToggleCategoryCommand = new RelayCommand(p => ToggleCategory(p as WindowsUpdateCategoryNode));
            OpenDiagnosticsCommand = new RelayCommand(_ => GoToDiagnostics?.Invoke());
        }

        // ── Свойства ─────────────────────────────────────────────────────────────

        private string _lastCheckedText = "Обновления ещё не проверялись";
        public string LastCheckedText { get => _lastCheckedText; private set => SetField(ref _lastCheckedText, value); }

        private string _statusText = "";
        public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }

        private IReadOnlyList<WindowsUpdateCategoryNode> _tree = Array.Empty<WindowsUpdateCategoryNode>();
        public IReadOnlyList<WindowsUpdateCategoryNode> Tree { get => _tree; private set => SetField(ref _tree, value); }

        private bool _showEmptyState;
        public bool ShowEmptyState { get => _showEmptyState; private set => SetField(ref _showEmptyState, value); }

        private string _emptyStateTitle = "Список обновлений пуст";
        public string EmptyStateTitle { get => _emptyStateTitle; private set => SetField(ref _emptyStateTitle, value); }

        private string _emptyStateSubtitle = "Нажмите «Проверить обновления», чтобы начать проверку";
        public string EmptyStateSubtitle { get => _emptyStateSubtitle; private set => SetField(ref _emptyStateSubtitle, value); }

        private bool _showOpenDiagnosticsButton;
        public bool ShowOpenDiagnosticsButton { get => _showOpenDiagnosticsButton; private set => SetField(ref _showOpenDiagnosticsButton, value); }

        private string _selectionSummaryText = "Выбрано: 0 патчей, 0 МБ";
        public string SelectionSummaryText { get => _selectionSummaryText; private set => SetField(ref _selectionSummaryText, value); }

        private bool _isInstallEnabled;
        public bool IsInstallEnabled { get => _isInstallEnabled; private set => SetField(ref _isInstallEnabled, value); }

        private bool _isSearching;
        public bool IsSearching
        {
            get => _isSearching;
            private set { SetField(ref _isSearching, value); CheckCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isInstalling;
        public bool IsInstalling
        {
            get => _isInstalling;
            private set { SetField(ref _isInstalling, value); CheckCommand.RaiseCanExecuteChanged(); }
        }

        // ── Первый запуск / поиск обновлений ────────────────────────────────────

        public async Task InitializeAsync()
        {
            if (ProfileService.Current.WindowsUpdateMode == "NotSet")
            {
                // Диалог выбора режима (WindowsUpdateModeDialog) не показывается: оба
                // варианта пока работают одинаково ("только уведомлять") — DownloadOnlyAsync
                // ещё не реализован (см. WindowsUpdateBackgroundService). Спрашивать выбор,
                // который ни на что не влияет, — чистая фрикция. Вернуть диалог вместе
                // с реализацией фоновой загрузки.
                ProfileService.Current.WindowsUpdateMode = "NotifyOnly";
                ProfileService.Save();
            }

            // Автопроверка при первом открытии вкладки — исходящий запрос к серверам
            // Microsoft без явного действия пользователя, ровно то, что параноидальный
            // режим обещает блокировать. Ручная кнопка «Проверить» НЕ гейтится —
            // пользователь сам инициирует запрос, и вкладка «Windows Update» в тексте
            // параноидального режима не заявлена как полностью заблокированная.
            if (ProfileService.Current.ParanoidMode)
            {
                StatusText = "Автопроверка отключена (параноидальный режим). Нажмите «Проверить обновления» вручную.";
                AppLogger.Write("[WindowsUpdate] Автопроверка при открытии вкладки пропущена: параноидальный режим");
                return;
            }

            await RunSearchAsync();
        }

        private async Task RunSearchAsync()
        {
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;

            IsSearching = true;
            StatusText = "⏳ Проверка обновлений...";
            Tree = Array.Empty<WindowsUpdateCategoryNode>();
            // Идёт проверка — это не «пусто», а «загрузка»; не показываем пустое
            // состояние поверх дерева, пока не появится финальный результат.
            ShowEmptyState = false;

            if (!_service.IsServiceRunning())
            {
                var startNow = MessageBox.Show(
                    "Служба Windows Update не запущена. Запустить её сейчас?",
                    "Служба остановлена", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (startNow == MessageBoxResult.Yes && !_service.TryStartService())
                {
                    StatusText = "❌ Не удалось запустить службу Windows Update.";
                    IsSearching = false;
                    ShowEmptyStateInfo("Служба Windows Update недоступна",
                        "Не удалось запустить службу — подробности в сообщении выше");
                    return;
                }
                if (startNow == MessageBoxResult.No)
                {
                    StatusText = "⚠ Служба Windows Update не запущена — проверка недоступна.";
                    IsSearching = false;
                    ShowEmptyStateInfo("Служба Windows Update недоступна",
                        "Нажмите «Проверить обновления» ещё раз, когда служба будет запущена");
                    return;
                }
            }

            try
            {
                var result = await _service.SearchAsync(ct);
                if (ct.IsCancellationRequested) return;

                IsSearching = false;
                LastCheckedText = $"Последняя проверка: {DateTime.Now:dd.MM.yyyy HH:mm}";

                if (!result.Success)
                {
                    StatusText = $"❌ {result.ErrorMessage}";
                    ShowEmptyStateInfo("Не удалось получить список обновлений",
                        "Подробности — в сообщении выше");
                    return;
                }

                if (result.Items.Count == 0)
                {
                    StatusText = "✅ Обновлений не найдено — система актуальна.";
                    AppLogger.Write("🛡️ Windows Update: обновлений не найдено");
                    UpdateSelectionSummary();
                    ShowEmptyStateInfo("Обновлений нет", "Система полностью обновлена", offerDiagnostics: false);
                    return;
                }

                StatusText = $"Найдено патчей: {result.Items.Count}";
                AppLogger.Write($"🛡️ Windows Update: найдено патчей — {result.Items.Count}");
                SetTree(WindowsUpdateCategoryTreeBuilder.Build(result.Items));
                UpdateSelectionSummary();
            }
            catch (OperationCanceledException)
            {
                // Поиск был вытеснен новым запросом — просто выходим,
                // новый поиск позаботится об обновлении UI.
            }
        }

        /// <summary>
        /// Показывает подсказку вместо пустого дерева обновлений. Вызывается на всех
        /// терминальных состояниях, где реальный список патчей не построен (служба
        /// недоступна / ошибка / всё установлено).
        /// </summary>
        /// <param name="offerDiagnostics">
        /// Показать ли кнопку перехода к «Диагностике». По умолчанию — да: пустой список
        /// после неудачной проверки почти всегда означает проблему, причины которой видны
        /// в журнале ошибок Windows Update. false — для исправной системы («Обновлений нет»).
        /// </param>
        private void ShowEmptyStateInfo(string title, string subtitle, bool offerDiagnostics = true)
        {
            EmptyStateTitle = title;
            EmptyStateSubtitle = subtitle;
            ShowOpenDiagnosticsButton = offerDiagnostics;
            ShowEmptyState = true;
        }

        /// <summary>
        /// Заменяет дерево целиком и подписывается на изменение IsChecked каждого патча —
        /// замена ручной синхронизации categoryCheck.IsChecked = category.IsChecked из
        /// оригинального обработчика клика по патчу. Старые подписки не отписываются явно:
        /// дерево заменяется целиком (как и оригинал перестраивал весь TreeView.Items),
        /// старые узлы становятся мусором вместе со своими подписчиками. internal — seam
        /// для юнит-тестов (реальный вызов только из успешной ветки RunSearchAsync).
        /// </summary>
        internal void SetTree(IReadOnlyList<WindowsUpdateCategoryNode> tree)
        {
            foreach (var category in tree)
            {
                foreach (var item in category.Items)
                {
                    item.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName != nameof(WindowsUpdateItemNode.IsChecked)) return;
                        WindowsUpdateCategoryTreeBuilder.RecalculateCategoryState(category);
                        UpdateSelectionSummary();
                    };
                }
            }
            Tree = tree;
        }

        private void UpdateSelectionSummary()
        {
            var selectedIds = WindowsUpdateCategoryTreeBuilder.GetSelectedUpdateIds(Tree);
            long totalBytes = WindowsUpdateCategoryTreeBuilder.GetSelectedTotalSizeBytes(Tree);
            SelectionSummaryText = $"Выбрано: {selectedIds.Count} патчей, {SizeFormatter.BytesToMB(totalBytes)}";
            IsInstallEnabled = selectedIds.Count > 0 && !WindowsUpdateService.IsBusy;
        }

        // ── Дерево категорий ─────────────────────────────────────────────────────

        /// <summary>
        /// Клик по чекбоксу категории. Тот же итог, что и в оригинальном обработчике
        /// (categoryCheck.IsChecked == true после встроенного цикла ToggleButton для
        /// IsThreeState), выраженный как чистая функция состояния ДО клика:
        /// снятая категория (false) при клике полностью выбирается; любая другая
        /// (true или частично null) — полностью снимается. См. спеку за разбором цикла.
        /// </summary>
        private void ToggleCategory(WindowsUpdateCategoryNode? category)
        {
            if (category == null) return;
            bool newState = category.IsChecked == false;
            WindowsUpdateCategoryTreeBuilder.ApplyCategoryCheck(category, newState);
        }

        // ── Установка ────────────────────────────────────────────────────────────

        private async Task RunInstallAsync()
        {
            if (IsInstalling) return;

            var selectedIds = WindowsUpdateCategoryTreeBuilder.GetSelectedUpdateIds(Tree);
            if (selectedIds.Count == 0) return;

            var eulaItems = WindowsUpdateCategoryTreeBuilder.GetItemsNeedingEula(Tree);
            long totalBytes = WindowsUpdateCategoryTreeBuilder.GetSelectedTotalSizeBytes(Tree);

            bool confirmed;
            if (eulaItems.Count > 0)
            {
                // EULA нескольких патчей может быть длинным — показываем в отдельном окне
                // с прокруткой (раньше склеивалось в один MessageBox и обрезалось по высоте).
                string header = $"Установить {selectedIds.Count} патчей ({SizeFormatter.BytesToMB(totalBytes)})?\n" +
                                "Может потребоваться перезагрузка после установки.";
                string eulaText = string.Join("\n\n----------------------------------------\n\n",
                    eulaItems.Select(i => $"{i.Title}:\n\n{i.EulaText}"));
                var dialog = new EulaConfirmWindow(header, eulaText) { Owner = OwnerWindowProvider?.Invoke() };
                confirmed = dialog.ShowDialog() == true;
            }
            else
            {
                string confirmText = $"Установить {selectedIds.Count} патчей ({SizeFormatter.BytesToMB(totalBytes)})?\n\n" +
                                      "Может потребоваться перезагрузка после установки.";
                confirmed = MessageBox.Show(confirmText, "Подтверждение установки",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            }
            if (!confirmed) return;

            IsInstalling = true;
            IsInstallEnabled = false;
            var progress = new Progress<WindowsUpdateProgress>(p =>
            {
                StatusText = $"{p.Phase}: {p.CurrentTitle} ({p.CompletedCount}/{p.TotalCount}, {p.PercentComplete}%)";
            });

            var outcome = await _service.InstallSelectedAsync(selectedIds, progress, CancellationToken.None);

            IsInstalling = false;
            if (!outcome.Success && outcome.Items.Count == 0)
            {
                // Отказ ещё до старта (занято/reboot-pending/пусто) — есть только общее сообщение.
                MessageBox.Show(outcome.ErrorMessage, "Установка не выполнена",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateSelectionSummary();
                return;
            }

            var resultWindow = new WindowsUpdateResultWindow(outcome) { Owner = OwnerWindowProvider?.Invoke() };
            resultWindow.ShowDialog();

            // После установки — обновить список (успешно поставленные больше не должны показываться).
            await RunSearchAsync();
        }
    }
}
```

- [ ] **Step 3: Написать `tests/Ven4Tools.Tests/WindowsUpdateViewModelTests.cs`**

```csharp
using System.Collections.Generic;
using Ven4Tools.Services.WindowsUpdate;
using Ven4Tools.ViewModels;
using Xunit;

namespace Ven4Tools.Tests
{
    public class WindowsUpdateViewModelTests
    {
        private static WindowsUpdateItem MakeItem(string id, long sizeBytes = 100) =>
            new() { UpdateId = id, Title = $"Патч {id}", SizeBytes = sizeBytes };

        [Fact]
        public void Конструктор_УстанавливаетДефолты()
        {
            var vm = new WindowsUpdateViewModel();

            Assert.Equal("Обновления ещё не проверялись", vm.LastCheckedText);
            Assert.Equal("", vm.StatusText);
            Assert.Empty(vm.Tree);
            Assert.False(vm.ShowEmptyState);
            Assert.Equal("Список обновлений пуст", vm.EmptyStateTitle);
            Assert.Equal("Нажмите «Проверить обновления», чтобы начать проверку", vm.EmptyStateSubtitle);
            Assert.False(vm.ShowOpenDiagnosticsButton);
            Assert.Equal("Выбрано: 0 патчей, 0 МБ", vm.SelectionSummaryText);
            Assert.False(vm.IsInstallEnabled);
            Assert.False(vm.IsSearching);
            Assert.False(vm.IsInstalling);
        }

        [Fact]
        public void CheckCommand_CanExecute_ИзначальноTrue()
        {
            var vm = new WindowsUpdateViewModel();
            Assert.True(vm.CheckCommand.CanExecute(null));
        }

        [Fact]
        public void OpenDiagnosticsCommand_ПоднимаетСобытие()
        {
            var vm = new WindowsUpdateViewModel();
            bool raised = false;
            vm.GoToDiagnostics += () => raised = true;

            vm.OpenDiagnosticsCommand.Execute(null);

            Assert.True(raised);
        }

        [Theory]
        [InlineData(false, true)]   // снятая категория — выбирается вся
        [InlineData(true, false)]   // полностью выбранная — снимается
        [InlineData(null, false)]   // частично выбранная (indeterminate) — снимается
        public void ToggleCategoryCommand_ВычисляетНовоеСостояниеПоПравилуОригинала(bool? before, bool expectedAfter)
        {
            var vm = new WindowsUpdateViewModel();
            var category = new WindowsUpdateCategoryNode
            {
                Name = "Тест",
                Items = new List<WindowsUpdateItemNode>
                {
                    new() { Item = MakeItem("1"), IsChecked = false },
                    new() { Item = MakeItem("2"), IsChecked = false }
                },
                IsChecked = before
            };

            vm.ToggleCategoryCommand.Execute(category);

            Assert.Equal(expectedAfter, category.IsChecked);
            Assert.All(category.Items, item => Assert.Equal(expectedAfter, item.IsChecked));
        }

        [Fact]
        public void SetTree_ИзменениеIsCheckedПатча_ПересчитываетКатегориюИСводку()
        {
            var vm = new WindowsUpdateViewModel();
            var item1 = new WindowsUpdateItemNode { Item = MakeItem("1"), IsChecked = false };
            var item2 = new WindowsUpdateItemNode { Item = MakeItem("2"), IsChecked = false };
            var category = new WindowsUpdateCategoryNode { Name = "Тест", Items = new List<WindowsUpdateItemNode> { item1, item2 }, IsChecked = false };

            vm.SetTree(new[] { category });

            item1.IsChecked = true;

            Assert.Null(category.IsChecked);
            Assert.StartsWith("Выбрано: 1 патчей", vm.SelectionSummaryText);

            item2.IsChecked = true;

            Assert.True(category.IsChecked);
            Assert.StartsWith("Выбрано: 2 патчей", vm.SelectionSummaryText);
        }

        [Fact]
        public void WindowsUpdateItemNode_IsChecked_ПоднимаетPropertyChanged()
        {
            var node = new WindowsUpdateItemNode { Item = MakeItem("1") };
            bool raised = false;
            node.PropertyChanged += (_, e) => raised = e.PropertyName == nameof(WindowsUpdateItemNode.IsChecked);

            node.IsChecked = true;

            Assert.True(raised);
        }

        [Fact]
        public void WindowsUpdateCategoryNode_HeaderText_ВключаетКоличество()
        {
            var category = new WindowsUpdateCategoryNode
            {
                Name = "Критические",
                Items = new List<WindowsUpdateItemNode> { new() { Item = MakeItem("1") }, new() { Item = MakeItem("2") } }
            };

            Assert.Equal("Критические (2)", category.HeaderText);
        }

        [Fact]
        public void WindowsUpdateItemNode_DisplayText_ВключаетНазвание()
        {
            var node = new WindowsUpdateItemNode { Item = MakeItem("1", 1_048_576) };

            Assert.Contains("Патч 1", node.DisplayText);
        }
    }
}
```

- [ ] **Step 4: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок, 0 предупреждений.

- [ ] **Step 5: Прогнать новые тесты и существующие тесты построителя дерева**

Run: `dotnet test tests/Ven4Tools.Tests -c Release --filter "FullyQualifiedName~WindowsUpdateViewModelTests|FullyQualifiedName~WindowsUpdateCategoryTreeBuilderTests"`
Expected: все зелёные — новые И существующие 154 строки тестов построителя дерева (INPC-правка аддитивна, не должна их сломать).

- [ ] **Step 6: Commit**

```bash
git add Ven4Tools/Services/WindowsUpdate/WindowsUpdateCategoryTreeBuilder.cs Ven4Tools/ViewModels/WindowsUpdateViewModel.cs tests/Ven4Tools.Tests/WindowsUpdateViewModelTests.cs
git commit -m "feat(windowsupdate): WindowsUpdateViewModel + INotifyPropertyChanged на узлах дерева + юнит-тесты"
```

---

### Task 2: Переписать `WindowsUpdateTab.xaml`/`.xaml.cs` на тонкую обёртку

**Files:**
- Modify: `Ven4Tools/Views/Tabs/WindowsUpdateTab.xaml`
- Modify: `Ven4Tools/Views/Tabs/WindowsUpdateTab.xaml.cs`

**Interfaces:**
- Consumes: `Ven4Tools.ViewModels.WindowsUpdateViewModel` (Task 1) — вся публичная поверхность.
- Produces: `WindowsUpdateTab` — единственный публичный член сверх конструктора: `event Action? GoToDiagnostics` (внешний контракт, `MainWindow.xaml.cs`).

- [ ] **Step 1: Переписать `Ven4Tools/Views/Tabs/WindowsUpdateTab.xaml`**

Полное содержимое файла:

```xml
<UserControl x:Class="Ven4Tools.Views.Tabs.WindowsUpdateTab"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:wu="clr-namespace:Ven4Tools.Services.WindowsUpdate"
             Background="{DynamicResource ContentBackground}">
    <UserControl.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis"/>
        <HierarchicalDataTemplate DataType="{x:Type wu:WindowsUpdateCategoryNode}" ItemsSource="{Binding Items}">
            <CheckBox Content="{Binding HeaderText}"
                      IsThreeState="True"
                      IsChecked="{Binding IsChecked, Mode=OneWay}"
                      Command="{Binding DataContext.ToggleCategoryCommand, RelativeSource={RelativeSource AncestorType=TreeView}}"
                      CommandParameter="{Binding}"/>
        </HierarchicalDataTemplate>
        <DataTemplate DataType="{x:Type wu:WindowsUpdateItemNode}">
            <CheckBox Content="{Binding DisplayText}" IsChecked="{Binding IsChecked, Mode=TwoWay}"/>
        </DataTemplate>
    </UserControl.Resources>
    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Header -->
        <Grid Grid.Row="0" Margin="0,0,0,12">
            <StackPanel>
                <TextBlock Text="Обновления Windows" Style="{StaticResource PageTitleStyle}"/>
                <TextBlock x:Name="txtLastChecked" Text="{Binding LastCheckedText}"
                           FontSize="12" Foreground="{DynamicResource TextSecondary}" Margin="0,4,0,0"/>
            </StackPanel>
            <Button x:Name="btnCheck" Content="Проверить обновления" Height="34" Padding="16,0"
                    ToolTip="Найдёт доступные обновления Windows. На этом шаге ничего не устанавливается."
                    HorizontalAlignment="Right" VerticalAlignment="Top"
                    Background="{DynamicResource CardBackground}" Foreground="{DynamicResource TextPrimary}"
                    BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1"
                    Command="{Binding CheckCommand}"/>
        </Grid>

        <TextBlock Grid.Row="1" x:Name="txtStatus" Text="{Binding StatusText}" TextWrapping="Wrap"
                   Foreground="{DynamicResource TextSecondary}" Margin="0,0,0,10"/>

        <Grid Grid.Row="2">
            <TreeView x:Name="treeUpdates"
                      ItemsSource="{Binding Tree}"
                      Background="{DynamicResource CardBackground}"
                      BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1"/>

            <!-- Пустое состояние: показывается, когда дерево обновлений пусто (служба
                 недоступна / ещё не проверяли / ошибка / всё установлено). Иконка —
                 тот же Segoe Fluent Icons, что уже используют кнопки/навигация в проекте.
                 IsHitTestVisible перенесён с панели на сами надписи: панель обязана
                 пропускать клики мимо себя к дереву, но кнопка перехода к диагностике
                 внутри неё должна нажиматься. -->
            <StackPanel x:Name="pnlUpdatesEmpty"
                        HorizontalAlignment="Center" VerticalAlignment="Center"
                        Visibility="{Binding ShowEmptyState, Converter={StaticResource BoolToVis}}">
                <TextBlock Text="&#xE72E;" FontFamily="Segoe Fluent Icons" FontSize="40"
                           IsHitTestVisible="False"
                           Foreground="{DynamicResource TextSecondary}" HorizontalAlignment="Center"/>
                <TextBlock x:Name="txtUpdatesEmptyTitle" Text="{Binding EmptyStateTitle}"
                           FontSize="16" FontWeight="SemiBold" IsHitTestVisible="False"
                           Foreground="{DynamicResource TextPrimary}"
                           HorizontalAlignment="Center" Margin="0,12,0,0"/>
                <TextBlock x:Name="txtUpdatesEmptySubtitle"
                           Text="{Binding EmptyStateSubtitle}"
                           FontSize="12" Foreground="{DynamicResource TextSecondary}"
                           IsHitTestVisible="False"
                           HorizontalAlignment="Center" Margin="0,4,0,0"
                           TextWrapping="Wrap" MaxWidth="320" TextAlignment="Center"/>
                <!-- Появляется только на проблемных исходах проверки (служба недоступна,
                     ошибка получения списка) — на исправной системе без обновлений
                     вопрос «почему не ставятся» не имеет смысла. -->
                <Button x:Name="btnOpenDiagnostics" Content="Почему обновления не ставятся? → Диагностика"
                        ToolTip="Откроет вкладку «Диагностика» с журналом ошибок Windows Update за 7 дней и очисткой кэша обновлений."
                        Height="34" Padding="16,0" Margin="0,16,0,0"
                        HorizontalAlignment="Center"
                        Visibility="{Binding ShowOpenDiagnosticsButton, Converter={StaticResource BoolToVis}}"
                        Background="{DynamicResource CardBackground}" Foreground="{DynamicResource TextPrimary}"
                        BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1"
                        Command="{Binding OpenDiagnosticsCommand}"/>
            </StackPanel>
        </Grid>

        <!-- Footer: выбор + установка -->
        <Border Grid.Row="3" Margin="0,12,0,0" Padding="12,8" Background="{StaticResource SurfaceCard}"
                BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="7">
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <TextBlock x:Name="txtSelectionSummary" Grid.Column="0" Text="{Binding SelectionSummaryText}"
                       VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}"/>
            <Button x:Name="btnInstall" Grid.Column="1" Content="Установить выбранные" Height="38"
                    Padding="24,0" IsEnabled="{Binding IsInstallEnabled}"
                    ToolTip="Скачает и установит отмеченные обновления Windows. После установки может потребоваться перезагрузка."
                    Background="{StaticResource BrandGreen}" Foreground="#06130D" FontWeight="SemiBold"
                    BorderThickness="0" Command="{Binding InstallCommand}"/>
        </Grid>
        </Border>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Переписать `Ven4Tools/Views/Tabs/WindowsUpdateTab.xaml.cs`**

Полное содержимое файла:

```csharp
using System;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «Обновления Windows» — тонкая обёртка над <see cref="WindowsUpdateViewModel"/>.
    /// Вся логика перенесена в ViewModel при MVVM-миграции (2026-08-26, десятая
    /// вкладка после Debloater/History/About/Activation/Network/Office/Installed/
    /// Diagnostics/System). Единственный публичный член сверх конструктора —
    /// event GoToDiagnostics (внешний контракт, MainWindow.xaml.cs).
    /// </summary>
    public partial class WindowsUpdateTab : UserControl
    {
        private readonly WindowsUpdateViewModel _viewModel = new();
        private bool _firstRunHandled;

        public event Action? GoToDiagnostics;

        public WindowsUpdateTab()
        {
            InitializeComponent();
            DataContext = _viewModel;
            _viewModel.OwnerWindowProvider = () => Window.GetWindow(this);
            _viewModel.GoToDiagnostics += () => GoToDiagnostics?.Invoke();

            Loaded += async (_, _) =>
            {
                if (_firstRunHandled) return;
                _firstRunHandled = true;
                await _viewModel.InitializeAsync();
            };
        }
    }
}
```

- [ ] **Step 3: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок, 0 предупреждений — во всех проектах, включая `Ven4Tools.ClientUITests`.

- [ ] **Step 4: Прогнать весь юнит-набор**

Run: `dotnet test tests/Ven4Tools.Tests -c Release`
Expected: без регрессий (было 490 после SystemTab + новые из `WindowsUpdateViewModelTests` — итоговое число проверить фактическим прогоном).

- [ ] **Step 5: Commit**

```bash
git add Ven4Tools/Views/Tabs/WindowsUpdateTab.xaml Ven4Tools/Views/Tabs/WindowsUpdateTab.xaml.cs
git commit -m "refactor(windowsupdate): WindowsUpdateTab — тонкая обёртка над WindowsUpdateViewModel"
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

- [ ] **Step 2: Юнит-тесты целиком на VenchWork**

Run (на VenchWork): `dotnet test tests/Ven4Tools.Tests -c Release`
Expected: без регрессий относительно числа тестов после Task 2.

- [ ] **Step 3: Существующие UI-тесты на VenchWork**

Run (на VenchWork): `dotnet test Ven4Tools.ClientUITests -c Release --filter "FullyQualifiedName~WindowsUpdateTabSmokeTests|FullyQualifiedName~Top5FeaturesUiTests|FullyQualifiedName~KeyButtonsSmokeTests"`
Expected: `WindowsUpdateTabSmokeTests` (открытие + `txtStatus`, реальная установка не вызывается), релевантная часть `Top5FeaturesUiTests`/`KeyButtonsSmokeTests` — зелёные.

**Если UI-прогон не укладывается в 10-15 минут** — не ждать дальше: ребутнуть VenchWork / подключить Opus 5 для диагностики / искать причину самостоятельно, начиная с `%LOCALAPPDATA%\Ven4Tools\crash_last.json` (см. `feedback_ui_test_hang_escalation` в памяти).

- [ ] **Step 4: Финальный коммит верификации**

```bash
git add -A
git status
git commit -m "test(windowsupdate): MVVM-миграция WindowsUpdateTab проверена на VenchWork" --allow-empty
```

- [ ] **Step 5: Финальное цельное ревью ветки**

Обязательный шаг перед мерджем. Пакет для ревью: `scripts/review-package <merge-base main mvvm-windowsupdatetab> HEAD`. **Явно поручить ревьюеру**:
1. Перепроверить формулу `newState = category.IsChecked == false` (единственная нетривиальная логика этой миграции) — самостоятельно проследить цикл `ToggleButton` для `IsThreeState="True"` (как это уже дважды делали ревьюеры этой серии для WPF-специфичных вопросов — вплоть до построения минимального стенда, если требуется).
2. Подписки item→category в `SetTree` — нет ли утечки/повторной подписки на одни и те же узлы при повторных поисках (дерево должно каждый раз заменяться целиком новыми объектами, не переиспользоваться).
3. `WindowsUpdateCategoryTreeBuilderTests.cs` (существующие 154 строки) — подтвердить, что все они по-прежнему проходят без единой правки.
4. Внешний контракт `MainWindow.xaml.cs:230-239` — не сломан.

- [ ] **Step 6: Merge + push в `main`** (без дополнительного вопроса — автономная сессия)

```bash
git checkout main
git merge --ff-only mvvm-windowsupdatetab
dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental
git push origin main
git branch -d mvvm-windowsupdatetab
```

Перед пушем — обязательно проверить все коммиты ветки на `Claude-Session`-трейлер: `git log main..mvvm-windowsupdatetab --format="%B" | grep -i claude` (должно быть пусто).

---

## После задачи

Смержено и запушено в `main`. Осталась одна вкладка на code-behind — `BenchmarkTab` (607 строк, 4 файла) — тот же процесс, новая ветка от `main`, после этого клиент Ven4Tools полностью на MVVM.
