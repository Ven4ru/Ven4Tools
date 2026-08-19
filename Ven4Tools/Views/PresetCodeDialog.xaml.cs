using System.Windows;
using System.Windows.Input;
using Ven4Tools.Services;

namespace Ven4Tools.Views
{
    public partial class PresetCodeDialog : Window
    {
        public string RawCode => txtCode.Text;

        public PresetCodeDialog()
        {
            InitializeComponent();
            txtCode.Focus();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            // Разбираем прямо здесь: код самодостаточен, ждать нечего, и ошибку
            // показываем в самом окне, не закрывая его и не теряя вставленное.
            var parsed = SitePresetService.Parse(txtCode.Text);
            if (!parsed.Success)
            {
                txtHint.Text = parsed.Error;
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
