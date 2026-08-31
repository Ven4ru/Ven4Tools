using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «Сеть» — тонкая обёртка над <see cref="NetworkViewModel"/>.
    /// Вся логика перенесена в ViewModel при MVVM-миграции (2026-08-25, пятая
    /// вкладка после DebloaterTab/HistoryTab/AboutTab/ActivationTab). Публичного
    /// контракта сверх конструктора нет.
    /// </summary>
    public partial class NetworkTab : System.Windows.Controls.UserControl
    {
        private readonly NetworkViewModel _viewModel = new();

        public NetworkTab()
        {
            InitializeComponent();
            DataContext = _viewModel;

            // Без флага «только один раз» намеренно: список адаптеров обязан быть свежим
            // при каждом показе (кабель могли подключить, пока вкладка была закрыта).
            // Но CanExecute (_ => !IsBusy) обязан соблюдаться и здесь: прямой
            // Execute(null) его обходил, и возврат на вкладку посреди полной диагностики
            // перечитывал адаптеры параллельно с ней.
            Loaded += (_, _) => TabInitGuard.RunSync(() =>
            {
                if (_viewModel.RefreshAdaptersCommand.CanExecute(null))
                    _viewModel.RefreshAdaptersCommand.Execute(null);
            }, "[NetworkTab] Ошибка обновления списка адаптеров");
        }
    }
}
