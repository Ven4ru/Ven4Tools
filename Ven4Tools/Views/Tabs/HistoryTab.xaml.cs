using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Services;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «История» — тонкая обёртка над <see cref="HistoryViewModel"/>.
    /// Вся логика перенесена в ViewModel при MVVM-миграции (2026-08-24, вторая
    /// вкладка после пилота DebloaterTab). Публичный контракт (RefreshAsync)
    /// сохранён без изменений — <c>MainWindow.NavigateToHistory</c> обращается
    /// к нему напрямую.
    /// </summary>
    public partial class HistoryTab : UserControl
    {
        private readonly HistoryViewModel _viewModel = new();
        private bool _historySubscribed;

        public HistoryTab()
        {
            InitializeComponent();
            DataContext = _viewModel;

            // Подписка на Changed — в Loaded с флагом, отписка в Unloaded: вкладка
            // кэшируется в MainWindow и переиспользуется, поэтому после Unloaded нужно
            // подписываться заново при каждом показе (иначе живое обновление истории
            // пропадало после первого ухода с вкладки). Отписка снимает утечку.
            Loaded += HistoryTab_Loaded;
            Unloaded += HistoryTab_Unloaded;

            txtHistorySearch.GotFocus  += (_, _) => { if (txtHistorySearch.Text == (string)txtHistorySearch.Tag) txtHistorySearch.Text = ""; };
            txtHistorySearch.LostFocus += (_, _) => { if (string.IsNullOrWhiteSpace(txtHistorySearch.Text)) txtHistorySearch.Text = (string)txtHistorySearch.Tag; };
            txtHistorySearch.Text = (string)txtHistorySearch.Tag;
        }

        private async void HistoryTab_Loaded(object sender, RoutedEventArgs e)
        {
            // Переподписка при каждом показе (после Unloaded подписка снимается).
            // Флаг защищает от повторной подписки, если Loaded сработает дважды подряд.
            if (!_historySubscribed)
            {
                InstallHistoryService.Instance.Changed += OnHistoryChanged;
                _historySubscribed = true;
            }
            await RefreshAsync();
        }

        private void HistoryTab_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_historySubscribed)
            {
                InstallHistoryService.Instance.Changed -= OnHistoryChanged;
                _historySubscribed = false;
            }
        }

        private void OnHistoryChanged() =>
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try { await RefreshAsync(); }
                catch (Exception ex) { AppLogger.Write(ex.Message); }
            });

        // Плейсхолдер в txtHistorySearch — не настоящий поиск, форвардим в
        // ViewModel только реальный запрос пользователя.
        private void TxtHistorySearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string text = txtHistorySearch.Text;
            _viewModel.SearchText = text == (string)txtHistorySearch.Tag ? "" : text;
        }

        public Task RefreshAsync() => _viewModel.RefreshAsync();
    }
}
