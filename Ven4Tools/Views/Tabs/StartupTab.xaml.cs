using System.Windows.Controls;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Раздел «Автозагрузка» — заголовок и <see cref="StartupPanel"/>. Раньше панель
    /// была вкладкой «Очистки»; вся логика по-прежнему в ней и в её ViewModel.
    /// </summary>
    public partial class StartupTab : UserControl
    {
        public StartupTab()
        {
            InitializeComponent();
        }
    }
}
