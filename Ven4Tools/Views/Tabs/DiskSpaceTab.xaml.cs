using System.Windows.Controls;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Раздел «Место и файлы» — заголовок и <see cref="DiskSpacePanel"/>. Раньше панель
    /// была вкладкой «Очистки»; вся логика по-прежнему в ней и в её ViewModel.
    /// </summary>
    public partial class DiskSpaceTab : UserControl
    {
        public DiskSpaceTab()
        {
            InitializeComponent();
        }
    }
}
