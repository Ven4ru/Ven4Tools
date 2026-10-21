using System.Windows.Controls;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Подраздел «Автозагрузка» вкладки «Очистка» — тонкая обёртка над
    /// <see cref="StartupViewModel"/>. Список читается при первом показе подраздела:
    /// он обходит реестр, папки и весь планировщик заданий и проверяет подписи файлов.
    /// </summary>
    public partial class StartupPanel : UserControl
    {
        private readonly StartupViewModel _viewModel = new();

        public StartupPanel()
        {
            InitializeComponent();
            DataContext = _viewModel;
            IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is true)
                    TabInitGuard.Run(_viewModel.LoadOnceAsync, "StartupPanel.LoadOnceAsync");
            };
        }
    }
}
