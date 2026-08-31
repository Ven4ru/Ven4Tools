using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ven4Tools.Helpers;
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

        // Золото звезды рейтинга — конвенция, а не цвет интерфейса: оно одинаково
        // читается на подложке любой темы и узнаётся как «оценка». А вот погашенная
        // звезда обязана следовать теме: зашитый тёмно-серый #444 на светлой подложке
        // «Светлой» темы выглядел контрастнее золотой — невыбранные звёзды казались
        // выбранными.
        private static readonly Brush StarActive = FrozenStar(0xFF, 0xC1, 0x07);
        private static readonly Brush StarPreview = FrozenStar(0xFF, 0xB3, 0x00);
        private static readonly Brush StarDimFallback = FrozenStar(0x44, 0x44, 0x44);

        private static Brush FrozenStar(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private void PaintStars(int count, bool preview = false)
        {
            Brush lit = preview ? StarPreview : StarActive;
            Brush dim = BrushResolver.Resolve("TextSecondary", StarDimFallback);

            for (int i = 0; i < _stars.Count; i++)
                _stars[i].Foreground = i < count ? lit : dim;
        }

        private void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            FeedbackService.Write(_rating, txtFeedback.Text.Trim());
            Close();
        }

        private void BtnSkip_Click(object sender, RoutedEventArgs e) => Close();
    }
}
