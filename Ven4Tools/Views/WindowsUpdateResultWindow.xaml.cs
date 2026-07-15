using System.Diagnostics;
using System.Linq;
using System.Windows;
using Ven4Tools.Services;
using Ven4Tools.Services.WindowsUpdate;

namespace Ven4Tools.Views
{
    public partial class WindowsUpdateResultWindow : Window
    {
        public WindowsUpdateResultWindow(WindowsUpdateInstallOutcome outcome)
        {
            InitializeComponent();

            int success = outcome.Items.Count(i => i.Success);
            int failed = outcome.Items.Count - success;

            txtSummary.Text = outcome.Success
                ? $"✅ Установлено патчей: {success}"
                : $"⚠ Установлено: {success}, не удалось: {failed}";

            foreach (var item in outcome.Items)
            {
                lstItems.Items.Add(item.Success
                    ? $"✅ {item.Title}"
                    : $"❌ {item.Title} — {item.ErrorMessage}");
            }

            if (!string.IsNullOrWhiteSpace(outcome.ErrorMessage))
                lstItems.Items.Add($"❌ {outcome.ErrorMessage}");

            if (outcome.RebootRequired)
            {
                txtSummary.Text += "\n\nТребуется перезагрузка для завершения установки.";
                btnRestartNow.Visibility = Visibility.Visible;
            }
            else
            {
                // Без ожидающей перезагрузки "Позже" звучит так, будто что-то ещё не сделано —
                // это финальный экран, а не отложенное действие.
                btnClose.Content = "Готово";
            }

            AppLogger.Write(outcome.Success
                ? $"🛡️ Windows Update: установлено патчей — {success}"
                : $"🛡️ Windows Update: установлено {success}, не удалось {failed}");
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnRestartNow_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo(Ven4Tools.Services.TrustedExecutablePaths.ShutdownExe, "/r /t 5") { UseShellExecute = true });
            Close();
        }
    }
}
