using System;
using System.Threading;
using System.Windows;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    /// <summary>
    /// Л8 «Диагностика соединения» — окно над <see cref="ConnectionDiagnostics"/>.
    /// Проверка запускается при открытии и не блокирует интерфейс.
    /// </summary>
    public partial class ConnectionDiagnosticsWindow : Window
    {
        private readonly CancellationTokenSource _cts = new();

        public ConnectionDiagnosticsWindow()
        {
            InitializeComponent();
            Loaded += (_, _) => Run();
            Closed += (_, _) => _cts.Cancel();
        }

        private async void Run()
        {
            btnRun.IsEnabled = btnCopy.IsEnabled = false;
            lstConclusions.ItemsSource = null;
            txtReport.Text = "";
            var progress = new Progress<string>(s => txtProgress.Text = s);
            try
            {
                var result = await ConnectionDiagnostics.RunAsync(progress, _cts.Token);
                txtProgress.Text = "Итог:";
                lstConclusions.ItemsSource = result.Conclusions;
                txtReport.Text = ConnectionDiagnostics.BuildReport(result,
                    LauncherInstallation.GetCurrentVersion(), Environment.OSVersion.Version.ToString());
                btnCopy.IsEnabled = true;
                LauncherLog.Write("[Диагностика соединения] " + string.Join(" | ", result.Conclusions));
            }
            catch (OperationCanceledException)
            {
                // Окно закрыто посреди проверки.
            }
            catch (Exception ex)
            {
                txtProgress.Text = $"Диагностика не выполнена: {ex.Message}";
                LauncherLog.Write($"[Диагностика соединения] Не выполнена: {ex}");
            }
            finally
            {
                if (!_cts.IsCancellationRequested) btnRun.IsEnabled = true;
            }
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(txtReport.Text); }
            catch (Exception ex) { LauncherLog.Write($"[Буфер обмена] Не удалось скопировать: {ex.Message}"); }
        }

        private void BtnRun_Click(object sender, RoutedEventArgs e) => Run();

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
