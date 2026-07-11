using System.Windows;

namespace Ven4Tools.Launcher
{
    public partial class SettingsWindow : Window
    {
        private readonly MainWindow _owner;

        public SettingsWindow(MainWindow owner, bool backgroundUpdates, bool startMinimized,
            bool autostart, bool autoUpdateClient)
        {
            InitializeComponent();
            _owner = owner;
            Sync(backgroundUpdates, startMinimized, autostart, autoUpdateClient);
        }

        // Programmatic IsChecked assignment does not raise Click — безопасно
        // вызывать в любой момент, не вызовет каскад Save.
        internal void Sync(bool backgroundUpdates, bool startMinimized, bool autostart, bool autoUpdateClient)
        {
            chkBackgroundUpdates.IsChecked = backgroundUpdates;
            chkStartMinimized.IsChecked    = startMinimized;
            chkAutostart.IsChecked         = autostart;
            rbAutoUpdateManual.IsChecked   = !autoUpdateClient;
            rbAutoUpdateAuto.IsChecked     = autoUpdateClient;
        }

        private void ChkBackgroundUpdates_Click(object sender, RoutedEventArgs e) =>
            _owner.OnBackgroundUpdatesChanged(chkBackgroundUpdates.IsChecked == true);

        private void ChkStartMinimized_Click(object sender, RoutedEventArgs e) =>
            _owner.OnStartMinimizedChanged(chkStartMinimized.IsChecked == true);

        private void ChkAutostart_Click(object sender, RoutedEventArgs e) =>
            _owner.OnAutostartChanged(chkAutostart.IsChecked == true);

        private void RbAutoUpdateMode_Click(object sender, RoutedEventArgs e) =>
            _owner.OnAutoUpdateClientChanged(rbAutoUpdateAuto.IsChecked == true);

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
