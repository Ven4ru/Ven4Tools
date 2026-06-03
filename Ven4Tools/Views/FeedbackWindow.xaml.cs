using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ven4Tools.Services;

namespace Ven4Tools.Views
{
    public partial class FeedbackWindow : Window
    {
        private int _rating = 0;
        private List<Button> _stars = new();

        public FeedbackWindow()
        {
            InitializeComponent();
            txtTitle.Text = $"Отзыв о Ven4Tools {ChannelService.InstalledVersion}";
            _stars = new List<Button> { star1, star2, star3, star4, star5 };
        }

        private void Star_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            _rating = int.Parse(btn.Tag.ToString()!);
            PaintStars(_rating);
            btnSend.IsEnabled = true;
        }

        private void Star_Hover(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is not Button btn) return;
            int hovered = int.Parse(btn.Tag.ToString()!);
            PaintStars(hovered, preview: true);
        }

        private void Star_Leave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            PaintStars(_rating);
        }

        private void PaintStars(int count, bool preview = false)
        {
            var color = preview
                ? Color.FromRgb(0xFF, 0xB3, 0x00)
                : Color.FromRgb(0xFF, 0xC1, 0x07);
            var dim = Color.FromRgb(0x44, 0x44, 0x44);

            for (int i = 0; i < _stars.Count; i++)
                _stars[i].Foreground = new SolidColorBrush(
                    i < count ? color : dim);
        }

        private void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            FeedbackService.Write(_rating, txtFeedback.Text.Trim());
            Close();
        }

        private void BtnSkip_Click(object sender, RoutedEventArgs e) => Close();
    }
}
