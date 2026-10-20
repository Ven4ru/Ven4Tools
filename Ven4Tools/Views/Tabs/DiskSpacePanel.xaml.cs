using System.Windows.Controls;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Раздел «Место и файлы» вкладки «Очистка»: «Освободить место на C» и «Кто держит файл».
    /// Тонкая обёртка над <see cref="DiskSpaceViewModel"/>; ViewModel создаётся здесь же,
    /// чтобы вкладке-хозяину хватало одной строки разметки <c>&lt;tabs:DiskSpacePanel/&gt;</c>.
    /// Подсчёт не запускается сам при показе: он читает весь диск и обращается к DISM,
    /// поэтому только по кнопке.
    /// </summary>
    public partial class DiskSpacePanel : UserControl
    {
        private readonly DiskSpaceViewModel _viewModel = new();

        public DiskSpacePanel()
        {
            InitializeComponent();
            DataContext = _viewModel;
        }
    }
}
