using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
    /// поведения.
    /// </summary>
    public sealed class WindowsUpdateViewModel : ViewModelBase
    {
        /// <summary>Окно-владелец для EulaConfirmWindow/WindowsUpdateResultWindow.</summary>
        public Func<Window?>? OwnerWindowProvider { get; set; }

        /// <summary>
        /// Запрос на переход к вкладке «Диагностика». Сама вкладка обновлений про
        /// навигацию не знает — переключением занимается MainWindow, как это уже
        /// сделано у OfficeTab.GoToActivation/DiagnosticsTab.GoToWindowsUpdate.
        /// </summary>
        public event Action? GoToDiagnostics;

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

        // Дефолт true — в оригинальном XAML у pnlUpdatesEmpty нет атрибута Visibility,
        // то есть подсказка видна сразу поверх пустого дерева, пока не начался первый
        // поиск (RunSearchAsync явно скрывает её первой строкой, как и оригинал делал
        // через pnlUpdatesEmpty.Visibility = Collapsed). Без этого дефолта сценарий
        // ParanoidMode (InitializeAsync выходит раньше RunSearchAsync) оставлял бы
        // пользователя перед пустым прямоугольником без объясняющей подсказки.
        private bool _showEmptyState = true;
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
            private set { if (SetField(ref _isSearching, value)) CheckCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isInstalling;
        public bool IsInstalling
        {
            get => _isInstalling;
            private set { if (SetField(ref _isInstalling, value)) CheckCommand.RaiseCanExecuteChanged(); }
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
                if (startNow == MessageBoxResult.Yes)
                {
                    // Через Task.Run: TryStartService ждёт запуска службы до 30 секунд
                    // (ServiceController.WaitForStatus), на UI-потоке это полностью
                    // замороженное окно «не отвечает». Сюда попадают не только по кнопке
                    // «Проверить», но и автоматически при первом открытии вкладки, а
                    // остановленный wuauserv — обычное дело после твиков вкладки «Очистка».
                    // Сам поиск обновлений уже уводится в пул тем же способом
                    // (WindowsUpdateComSource.SearchAsync).
                    if (!await Task.Run(_service.TryStartService))
                    {
                        StatusText = "❌ Не удалось запустить службу Windows Update.";
                        IsSearching = false;
                        ShowEmptyStateInfo("Служба Windows Update недоступна",
                            "Не удалось запустить службу — подробности в сообщении выше");
                        return;
                    }
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
