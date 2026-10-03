using System.Collections.Generic;
using System.Windows.Controls;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Каталог нового интерфейса — карточки и панель «Ваш набор». Своей логики нет:
    /// вид создаёт и держит <see cref="CatalogTab"/>, от него же наследуется DataContext
    /// (тот же <see cref="CatalogViewModel"/>, что у прежнего списка).
    /// </summary>
    public partial class CatalogCardsView : UserControl
    {
        public CatalogCardsView(Dictionary<string, CategoryHeaderViewModel> categoryHeaders)
        {
            InitializeComponent();

            // Тот же приём, что в CatalogTab: словарь заголовков передаётся конвертеру
            // напрямую, потому что у ресурсов XAML нет внедрения зависимостей.
            if (Resources["CategoryHeaderConverter"] is CategoryNameToHeaderConverter converter)
                converter.Headers = categoryHeaders;
        }
    }
}
