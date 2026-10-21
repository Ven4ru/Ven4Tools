using System.Windows;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    /// <summary>
    /// Блоки «Прокси», «Подлинность», «Диагностика соединения» окна настроек (Л6–Л8).
    /// Сами проверки и настройка — в отдельных окнах; здесь только кнопки и строка
    /// текущего режима прокси.
    /// </summary>
    public partial class SettingsWindow
    {
        private void InitTrustNetworkBlocks() => RefreshProxyStatus();

        private void RefreshProxyStatus()
        {
            if (LauncherProxy.Mode == ProxyMode.Manual && !LauncherProxy.ManualAllowed)
                txtProxyStatus.Text = "Сейчас: системный — как настроено в Windows. Ручной прокси временно отключён разработчиком.";
            else
                txtProxyStatus.Text = LauncherProxy.EffectiveMode == ProxyMode.Manual
                    ? "Сейчас: вручную (адрес задан в лаунчере)."
                    : "Сейчас: системный — как настроено в Windows.";
        }

        private void BtnProxySettings_Click(object sender, RoutedEventArgs e)
        {
            var win = new ProxySettingsWindow { Owner = this };
            win.ShowDialog();
            RefreshProxyStatus();
        }

        private void BtnAuthenticity_Click(object sender, RoutedEventArgs e)
        {
            new AuthenticityWindow(_owner.ClientPathForTrustChecks) { Owner = this }.Show();
        }

        private void BtnConnectionDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            new ConnectionDiagnosticsWindow { Owner = this }.Show();
        }
    }
}
