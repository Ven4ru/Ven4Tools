using System.Windows;
using System.Windows.Input;

namespace Ven4Tools.Views
{
    public partial class PresetCodeDialog : Window
    {
        public string Code => txtCode.Text.Trim();

        public PresetCodeDialog()
        {
            InitializeComponent();
            txtCode.Focus();
        }

        private void Load_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtCode.Text)) return;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void TxtCode_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Load_Click(sender, e);
        }
    }
}
