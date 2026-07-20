using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    public partial class SettingsWindow : Window
    {
        private readonly MainWindow _owner;

        // Программная установка SelectedIndex (в Sync) поднимает SelectionChanged, в
        // отличие от Click у чекбоксов — глушим обработчик на время синхронизации,
        // чтобы не было каскада Save/обратной записи того же значения.
        private bool _suppressSourceChange;

        public SettingsWindow(MainWindow owner, bool backgroundUpdates, bool startMinimized,
            bool autostart, bool autoUpdateClient, DownloadSource downloadSource)
        {
            InitializeComponent();
            _owner = owner;
            Sync(backgroundUpdates, startMinimized, autostart, autoUpdateClient, downloadSource);
        }

        // Programmatic IsChecked assignment does not raise Click — безопасно
        // вызывать в любой момент, не вызовет каскад Save.
        internal void Sync(bool backgroundUpdates, bool startMinimized, bool autostart,
            bool autoUpdateClient, DownloadSource downloadSource)
        {
            chkBackgroundUpdates.IsChecked = backgroundUpdates;
            chkStartMinimized.IsChecked    = startMinimized;
            chkAutostart.IsChecked         = autostart;
            rbAutoUpdateManual.IsChecked   = !autoUpdateClient;
            rbAutoUpdateAuto.IsChecked     = autoUpdateClient;

            // Порядок пунктов ComboBox совпадает с порядком членов enum DownloadSource:
            // индекс == (int)значение.
            _suppressSourceChange = true;
            cmbDownloadSource.SelectedIndex = (int)downloadSource;
            _suppressSourceChange = false;
        }

        private void ChkBackgroundUpdates_Click(object sender, RoutedEventArgs e) =>
            _owner.OnBackgroundUpdatesChanged(chkBackgroundUpdates.IsChecked == true);

        private void ChkStartMinimized_Click(object sender, RoutedEventArgs e) =>
            _owner.OnStartMinimizedChanged(chkStartMinimized.IsChecked == true);

        private void ChkAutostart_Click(object sender, RoutedEventArgs e) =>
            _owner.OnAutostartChanged(chkAutostart.IsChecked == true);

        private void RbAutoUpdateMode_Click(object sender, RoutedEventArgs e) =>
            _owner.OnAutoUpdateClientChanged(rbAutoUpdateAuto.IsChecked == true);

        private void CmbDownloadSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSourceChange) return;
            if (cmbDownloadSource.SelectedIndex < 0) return;
            _owner.OnDownloadSourceChanged((DownloadSource)cmbDownloadSource.SelectedIndex);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
