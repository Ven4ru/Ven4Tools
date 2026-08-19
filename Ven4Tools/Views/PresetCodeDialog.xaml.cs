using System.Windows;
using System.Windows.Input;
using Ven4Tools.Services;

namespace Ven4Tools.Views
{
    public partial class PresetCodeDialog : Window
    {
        public string Code => SitePresetService.NormalizeCode(txtCode.Text);

        public PresetCodeDialog()
        {
            InitializeComponent();
            txtCode.Focus();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            // Форму кода проверяем до закрытия окна: очевидную опечатку показываем
            // сразу, не гоняя человека через сетевой запрос и его таймаут.
            if (!SitePresetService.LooksLikeCode(txtCode.Text))
            {
                txtHint.Text = "Код состоит из 5 знаков после «V4T-», например V4T-6CRWK. " +
                               "В нём не бывает 0, 1, 5, 8 и букв O, I, L, S, B.";
                txtHint.Foreground = (System.Windows.Media.Brush)FindResource("StatusDanger");
                txtCode.Focus();
                txtCode.SelectAll();
                return;
            }
            DialogResult = true;
        }

        private void Code_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Ok_Click(sender, e);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
