using System.Windows;

namespace Ven4Tools.Views
{
    /// <summary>Диалог запроса имени для нового снапшота конфигурации.</summary>
    public partial class SnapshotNameDialog : Window
    {
        public string SnapshotName => txtName.Text.Trim();

        public SnapshotNameDialog(int tweakCount, int presetCount)
        {
            InitializeComponent();
            txtSummary.Text = $"твиков: {tweakCount}, пресетов: {presetCount}";
            txtName.Text = $"Снапшот {System.DateTime.Now:dd.MM HH:mm}";
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
