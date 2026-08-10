using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «Очистка» — только UI: фильтр по категориям, отметки, прогресс и кнопки.
    /// Список твиков живёт в <see cref="DebloatCatalog"/>, а сами системные операции
    /// (Appx, реестр, службы, PowerShell) — в <see cref="DebloatTweakExecutor"/>.
    /// </summary>
    public partial class DebloaterTab : UserControl
    {
        private readonly List<DebloatItem> _allItems = DebloatCatalog.BuildItems();
        private CancellationTokenSource? _cts;

        public DebloaterTab()
        {
            InitializeComponent();
            Loaded += (_, _) => ApplyFilter();
        }

        // ── UI helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Элементы, показанные текущим фильтром. Вынесено отдельно, потому что этот
        /// же набор нужен кнопке «Все»: она обязана отмечать ровно то, что пользователь
        /// видит на экране, а не весь список целиком.
        /// </summary>
        private List<DebloatItem> GetFilteredItems()
        {
            string cat = "all";
            if (rbApps.IsChecked == true)     cat = "app";
            if (rbPrivacy.IsChecked == true)  cat = "privacy";
            if (rbServices.IsChecked == true) cat = "service";

            return cat == "all"
                ? _allItems.ToList()
                : _allItems.Where(i => i.Category == cat).ToList();
        }

        private void ApplyFilter()
        {
            if (lstDebloat == null) return;

            lstDebloat.ItemsSource = GetFilteredItems();
        }

        private void FilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

        // Отмечаем только видимые сейчас действия. Раньше отмечались все 35 сразу:
        // пользователь, выбрав фильтр «Приложения» и нажав «Все», молча ставил галки
        // ещё и на правки реестра и на отключение служб (DiagTrack, SysMain,
        // dmwappushservice), которых на экране не было — и «Применить» их выполняло.
        // Подсказка кнопки при этом всегда обещала «показанные текущим фильтром».
        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in GetFilteredItems()) item.IsSelected = true;
            ApplyFilter();
        }

        private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
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
        /// Применяет твики по идентификаторам тем же путём, что и обычная кнопка
        /// «Применить» (удаление Appx, реестр, службы). Используется восстановлением
        /// снапшота конфигурации. Неизвестные идентификаторы пропускаются.
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

        private async void BtnApplyDebloat_Click(object sender, RoutedEventArgs e)
        {
            var selected = _allItems.Where(i => i.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Ничего не выбрано.", "Debloater",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            btnApplyDebloat.IsEnabled = false;
            progressDebloat.Visibility = Visibility.Visible;
            progressDebloat.Value = 0;

            // try/finally: любое исключение в процессе (зависший PowerShell, сбой
            // сервиса и т.п.) не должно оставить кнопку и прогресс-бар навсегда
            // заблокированными — состояние UI восстанавливается в любом случае.
            try
            {
                // Единый диалог: подтверждение действия (Отмена = прервать) + предложение
                // точки восстановления. Раньше здесь было два подряд диалога с одинаковым
                // текстом «Будет применено N действий» — предупреждение о рисках свёрнуто
                // в этот же вопрос.
                var hasRisky = selected.Any(i => i.Risk is "caution" or "moderate");
                var rpOutcome = await UiGuards.ConfirmAndCreateRestorePointAsync(
                    $"Будет применено {selected.Count} действий.{(hasRisky ? "\n\n⚠️ Среди них есть умеренные/опасные операции." : "")}\n\nСоздать точку восстановления Windows перед очисткой?",
                    "Ven4Tools — перед очисткой системы");
                if (rpOutcome == RestorePointOutcome.Cancelled)
                {
                    txtDebloatStatus.Text = "Отменено";
                    progressDebloat.Visibility = Visibility.Collapsed;
                    return;
                }

                _cts = new CancellationTokenSource();
                // L10: показываем кнопку отмены на время длинной операции.
                btnCancelDebloat.Visibility = Visibility.Visible;
                btnCancelDebloat.IsEnabled = true;
                int done = 0;
                int succeeded = 0;

                foreach (var item in selected)
                {
                    if (_cts.Token.IsCancellationRequested) break;

                    txtDebloatStatus.Text = $"⚙️ {item.Name}...";
                    progressDebloat.Value = (double)done / selected.Count * 100;

                    bool ok = await DebloatTweakExecutor.ApplyItemAsync(item.Category, item.Id, item.Name, _cts.Token);
                    AppLogger.Write($"{(ok ? "✅" : "❌")} {item.Name}");
                    if (ok) succeeded++;
                    done++;
                }

                if (_cts.Token.IsCancellationRequested)
                {
                    txtDebloatStatus.Text = $"⏹ Остановлено: применено {succeeded} из {selected.Count}";
                }
                else
                {
                    progressDebloat.Value = 100;
                    txtDebloatStatus.Text = $"✅ Готово: применено {succeeded} из {selected.Count}";
                }
            }
            finally
            {
                btnApplyDebloat.IsEnabled = true;
                btnCancelDebloat.Visibility = Visibility.Collapsed;
                _cts?.Dispose(); _cts = null;
            }
        }

        // L10: отмена применения твиков — прерывает цикл после текущего элемента.
        private void BtnCancelDebloat_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            btnCancelDebloat.IsEnabled = false;
            txtDebloatStatus.Text = "⏹ Останавливаю...";
        }
    }
}
