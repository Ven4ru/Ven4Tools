using System.IO;
using System.Windows;
using Ven4Tools.Models;
using Ven4Tools.Services;

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

        private async void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtName.Text))
            {
                MessageBox.Show("Введите название.", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Хеш фиксируется в момент добавления: приложение устанавливается elevated
            // (Verb=runas), а apps.json лежит в LocalAppData — доступен на запись любому
            // не-elevated процессу пользователя. Без пиннинга подмена LocalInstallerPath/
            // самого файла между добавлением и повторным запуском из списка привела бы
            // к запуску произвольного exe с правами администратора.
            string sha256;
            try
            {
                sha256 = await HashHelper.ComputeSha256Async(_filePath);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Не удалось прочитать файл: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
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
                Sha256             = sha256,
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
