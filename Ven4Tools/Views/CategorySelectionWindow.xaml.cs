using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ven4Tools.Services;

namespace Ven4Tools.Views
{
    public partial class CategorySelectionWindow : Window
    {
        private string _selected = "";
        private static readonly SolidColorBrush AccentBorder =
            new SolidColorBrush(Color.FromRgb(0, 120, 212));

        public CategorySelectionWindow()
        {
            InitializeComponent();
        }

        private void Card_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border card) return;

            _selected = card.Tag?.ToString() ?? "";

            // Reset all cards
            foreach (var c in new[] { cardBasic, cardExtended, cardFull })
            {
                c.BorderBrush = (Brush)Application.Current.FindResource("BorderBrush");
                c.BorderThickness = new Thickness(2);
            }

            // Highlight selected
            card.BorderBrush = AccentBorder;
            card.BorderThickness = new Thickness(3);

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
