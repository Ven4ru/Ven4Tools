using System.Windows;

namespace Ven4Tools.Views
{
    public partial class PresetSaveDialog : Window
    {
        public string PresetName        => txtName.Text.Trim();
        public string PresetDescription => txtDescription.Text.Trim();

        public PresetSaveDialog(int appCount)
        {
            InitializeComponent();
            txtCount.Text = $"{appCount} прил.";
            txtName.Focus();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtName.Text))
            {
                txtName.Focus();
                return;
            }
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
