using System.Windows;

namespace Ven4Tools.Views
{
    // Припарковано (round 37, 2026-08-15): показ этого диалога отключён —
    // WindowsUpdateTab.xaml.cs больше не создаёт его, режим всегда "NotifyOnly".
    // Файл не удалён — вернётся вместе с реализацией
    // IWindowsUpdateSource.DownloadOnlyAsync, см. CHANGELOG.md [Не выпущено]
    // и WindowsUpdateBackgroundService.CheckOnceAsync (ветка "NotifyAndDownload").
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
