using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ven4Tools.Helpers;
using Ven4Tools.Services;

namespace Ven4Tools.Views
{
    public partial class CategorySelectionWindow : Window
    {
        private string _selected = "";

        public CategorySelectionWindow()
        {
            InitializeComponent();
        }

        private void Card_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border card) return;

            _selected = card.Tag?.ToString() ?? "";

            // Сбрасываем оформление всех карточек
            foreach (var c in new[] { cardBasic, cardExtended, cardFull })
            {
                c.BorderBrush = (Brush)Application.Current.FindResource("BorderBrush");
                c.BorderThickness = new Thickness(2);
            }

            // Подсвечиваем выбранную акцентом текущей темы, а не зашитым синим:
            // окно выбора профиля каталога открывается уже после ThemeService.Apply().
            // Толщина рамки та же (2), что у невыбранных: при 3 содержимое выбранной
            // карточки съезжало на пиксель внутрь относительно соседних.
            card.BorderBrush = BrushResolver.Resolve("AccentColor");

            btnContinue.IsEnabled = true;
        }

        private void BtnContinue_Click(object sender, RoutedEventArgs e)
        {
            ProfileService.Current.CatalogMode = _selected;
            ProfileService.Current.HasSelectedCategory = true;
            ProfileService.Save();
            DialogResult = true;
        }
    }
}
