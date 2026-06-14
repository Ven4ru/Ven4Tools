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

        // Конструктор для редактирования существующего пресета
        public PresetSaveDialog(string name, string description)
        {
            InitializeComponent();
            txtName.Text        = name;
            txtDescription.Text = description;
            txtCount.Visibility = System.Windows.Visibility.Collapsed;
            txtName.Focus();
            txtName.SelectAll();
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
