using System.Windows;
using System.Windows.Controls;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «Активация» — тонкая обёртка над <see cref="ActivationViewModel"/>.
    /// Вся логика перенесена в ViewModel при MVVM-миграции (2026-08-25, четвёртая
    /// вкладка после DebloaterTab/HistoryTab/AboutTab). Публичного контракта сверх
    /// конструктора нет.
    /// </summary>
    public partial class ActivationTab : UserControl
    {
        private readonly ActivationViewModel _viewModel = new();

        public ActivationTab()
        {
            InitializeComponent();
            DataContext = _viewModel;
            _viewModel.OwnerWindowProvider = () => Window.GetWindow(this);

            // Без флага «только один раз» намеренно: пользователь уходит с вкладки к
            // внешнему средству активации и возвращается — статус обязан быть свежим,
            // ровно как у вкладки «История». Проверка дешёвая и сама себя ограничивает
            // по времени (WMI + OSPP.VBS с таймаутом).
            Loaded += (_, _) => TabInitGuard.Run(
                _viewModel.CheckActivationStatusAsync,
                "[ActivationTab] Ошибка проверки статуса активации");
        }
    }
}
