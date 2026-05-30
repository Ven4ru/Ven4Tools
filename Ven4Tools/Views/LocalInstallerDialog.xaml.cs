using System.IO;
using System.Windows;
using Ven4Tools.Models;

namespace Ven4Tools.Views
{
    public partial class LocalInstallerDialog : Window
    {
        public AppInfo? Result { get; private set; }
        private readonly string _filePath;

        public LocalInstallerDialog(string filePath)
        {
            InitializeComponent();
            _filePath = filePath;

            txtFilePath.Text = filePath;
            txtName.Text     = Path.GetFileNameWithoutExtension(filePath);

            foreach (AppCategory cat in System.Enum.GetValues(typeof(AppCategory)))
                cmbCategory.Items.Add(cat.ToString());
            cmbCategory.SelectedIndex = 0;

            txtName.Focus();
            txtName.SelectAll();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtName.Text))
            {
                MessageBox.Show("Введите название.", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Result = new AppInfo
            {
                Id                 = "User." + System.Guid.NewGuid().ToString("N").Substring(0, 8),
                DisplayName        = txtName.Text.Trim(),
                Category           = System.Enum.TryParse<AppCategory>(
                                         cmbCategory.SelectedItem?.ToString() ?? "",
                                         out var cat) ? cat : AppCategory.Другое,
                LocalInstallerPath = _filePath,
                InstallerUrls      = new System.Collections.Generic.List<string>(),
                IsUserAdded        = true
            };
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
