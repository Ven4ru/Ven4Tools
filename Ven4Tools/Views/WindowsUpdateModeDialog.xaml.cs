using System.Windows;

namespace Ven4Tools.Views
{
    public partial class WindowsUpdateModeDialog : Window
    {
        public string SelectedMode { get; private set; } = "NotifyOnly";

        public WindowsUpdateModeDialog()
        {
            InitializeComponent();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            SelectedMode = rbNotifyAndDownload.IsChecked == true ? "NotifyAndDownload" : "NotifyOnly";
            DialogResult = true;
            Close();
        }
    }
}
