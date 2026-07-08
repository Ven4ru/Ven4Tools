using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Services;
using Ven4Tools.Services.WindowsUpdate;
using Ven4Tools.Views;

namespace Ven4Tools.Views.Tabs
{
    public partial class WindowsUpdateTab : UserControl
    {
        private readonly WindowsUpdateService _service = new();
        private System.Collections.Generic.IReadOnlyList<WindowsUpdateCategoryNode> _tree =
            Array.Empty<WindowsUpdateCategoryNode>();
        private CancellationTokenSource? _searchCts;
        private bool _firstRunHandled;

        public WindowsUpdateTab()
        {
            InitializeComponent();
            Loaded += WindowsUpdateTab_Loaded;
        }

        private async void WindowsUpdateTab_Loaded(object sender, RoutedEventArgs e)
        {
            if (_firstRunHandled) return;
            _firstRunHandled = true;

            if (ProfileService.Current.WindowsUpdateMode == "NotSet")
            {
                var dialog = new WindowsUpdateModeDialog { Owner = Window.GetWindow(this) };
                if (dialog.ShowDialog() == true)
                {
                    ProfileService.Current.WindowsUpdateMode = dialog.SelectedMode;
                    ProfileService.Save();
                }
                else
                {
                    // Пользователь закрыл диалог без выбора — считаем "только уведомлять"
                    // как самый ненавязчивый вариант по умолчанию, не переспрашиваем каждый раз.
                    ProfileService.Current.WindowsUpdateMode = "NotifyOnly";
                    ProfileService.Save();
                }
            }

            await RunSearchAsync();
        }

        private async void BtnCheck_Click(object sender, RoutedEventArgs e) => await RunSearchAsync();

        private async Task RunSearchAsync()
        {
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;

            btnCheck.IsEnabled = false;
            txtStatus.Text = "⏳ Проверка обновлений...";
            treeUpdates.Items.Clear();

            if (!_service.IsServiceRunning())
            {
                var startNow = MessageBox.Show(
                    "Служба Windows Update не запущена. Запустить её сейчас?",
                    "Служба остановлена", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (startNow == MessageBoxResult.Yes && !_service.TryStartService())
                {
                    txtStatus.Text = "❌ Не удалось запустить службу Windows Update.";
                    btnCheck.IsEnabled = true;
                    return;
                }
                if (startNow == MessageBoxResult.No)
                {
                    txtStatus.Text = "⚠ Служба Windows Update не запущена — проверка недоступна.";
                    btnCheck.IsEnabled = true;
                    return;
                }
            }

            try
            {
                var result = await _service.SearchAsync(ct);
                if (ct.IsCancellationRequested) return;

                btnCheck.IsEnabled = true;
                txtLastChecked.Text = $"Последняя проверка: {DateTime.Now:dd.MM.yyyy HH:mm}";

                if (!result.Success)
                {
                    txtStatus.Text = $"❌ {result.ErrorMessage}";
                    return;
                }

                if (result.Items.Count == 0)
                {
                    txtStatus.Text = "✅ Обновлений не найдено — система актуальна.";
                    AppLogger.Write("🛡️ Windows Update: обновлений не найдено");
                    UpdateSelectionSummary();
                    return;
                }

                txtStatus.Text = $"Найдено патчей: {result.Items.Count}";
                AppLogger.Write($"🛡️ Windows Update: найдено патчей — {result.Items.Count}");
                _tree = WindowsUpdateCategoryTreeBuilder.Build(result.Items);
                RenderTree();
            }
            catch (OperationCanceledException)
            {
                // Поиск был вытеснен новым запросом — просто выходим,
                // новый поиск позаботится об обновлении UI.
            }
        }

        private void RenderTree()
        {
            treeUpdates.Items.Clear();
            foreach (var category in _tree)
            {
                var categoryItem = new TreeViewItem { IsExpanded = false };
                var categoryCheck = new CheckBox
                {
                    Content = $"{category.Name} ({category.Items.Count})",
                    IsThreeState = true,
                    IsChecked = category.IsChecked
                };
                categoryCheck.Click += (_, _) =>
                {
                    bool newState = categoryCheck.IsChecked == true;
                    WindowsUpdateCategoryTreeBuilder.ApplyCategoryCheck(category, newState);
                    RenderTree(); // перерисовать — проще и надёжнее ручной синхронизации чекбоксов детей
                };
                categoryItem.Header = categoryCheck;

                foreach (var itemNode in category.Items)
                {
                    var itemCheck = new CheckBox
                    {
                        Content = $"{itemNode.Item.Title}" +
                                  (itemNode.Item.KbArticleIds.Count > 0
                                      ? $" (KB{string.Join(", KB", itemNode.Item.KbArticleIds)})"
                                      : "") +
                                  $" — {FormatSize(itemNode.Item.SizeBytes)}",
                        IsChecked = itemNode.IsChecked
                    };
                    itemCheck.Click += (_, _) =>
                    {
                        itemNode.IsChecked = itemCheck.IsChecked == true;
                        WindowsUpdateCategoryTreeBuilder.RecalculateCategoryState(category);
                        UpdateSelectionSummary();
                        // Обновляем только состояние чекбокса категории, без полной перерисовки дерева,
                        // чтобы не сворачивать раскрытые узлы при каждом клике по патчу.
                        categoryCheck.IsChecked = category.IsChecked;
                    };
                    categoryItem.Items.Add(new TreeViewItem { Header = itemCheck });
                }

                treeUpdates.Items.Add(categoryItem);
            }
            UpdateSelectionSummary();
        }

        private void UpdateSelectionSummary()
        {
            var selectedIds = WindowsUpdateCategoryTreeBuilder.GetSelectedUpdateIds(_tree);
            long totalBytes = WindowsUpdateCategoryTreeBuilder.GetSelectedTotalSizeBytes(_tree);
            txtSelectionSummary.Text = $"Выбрано: {selectedIds.Count} патчей, {FormatSize(totalBytes)}";
            btnInstall.IsEnabled = selectedIds.Count > 0 && !WindowsUpdateService.IsBusy;
        }

        private static string FormatSize(long bytes) =>
            bytes <= 0 ? "0 МБ" : $"{bytes / 1024.0 / 1024.0:F1} МБ";

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            var selectedIds = WindowsUpdateCategoryTreeBuilder.GetSelectedUpdateIds(_tree);
            if (selectedIds.Count == 0) return;

            var eulaItems = WindowsUpdateErrorMapper.GetItemsNeedingEula(_tree);
            long totalBytes = WindowsUpdateCategoryTreeBuilder.GetSelectedTotalSizeBytes(_tree);

            string confirmText = $"Установить {selectedIds.Count} патчей ({FormatSize(totalBytes)})?\n\n" +
                                  "Может потребоваться перезагрузка после установки.";
            if (eulaItems.Count > 0)
            {
                confirmText += "\n\nЛицензионные соглашения выбранных обновлений:\n\n" +
                    string.Join("\n\n---\n\n", eulaItems.Select(i => $"{i.Title}:\n{i.EulaText}"));
            }

            var confirmed = MessageBox.Show(confirmText, "Подтверждение установки",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirmed != MessageBoxResult.Yes) return;

            btnInstall.IsEnabled = false;
            btnCheck.IsEnabled = false;
            var progress = new Progress<WindowsUpdateProgress>(p =>
            {
                txtStatus.Text = $"{p.Phase}: {p.CurrentTitle} ({p.CompletedCount}/{p.TotalCount}, {p.PercentComplete}%)";
            });

            var outcome = await _service.InstallSelectedAsync(selectedIds, progress, CancellationToken.None);

            btnCheck.IsEnabled = true;
            if (!outcome.Success && outcome.Items.Count == 0)
            {
                // Отказ ещё до старта (занято/reboot-pending/пусто) — есть только общее сообщение.
                MessageBox.Show(outcome.ErrorMessage, "Установка не выполнена",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateSelectionSummary();
                return;
            }

            var resultWindow = new WindowsUpdateResultWindow(outcome) { Owner = Window.GetWindow(this) };
            resultWindow.ShowDialog();

            // После установки — обновить список (успешно поставленные больше не должны показываться).
            await RunSearchAsync();
        }
    }
}
