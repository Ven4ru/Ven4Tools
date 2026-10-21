using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Ven4Tools.Launcher.Helpers;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    /// <summary>
    /// Л7 «Проверить подлинность» — окно над <see cref="AuthenticityChecker"/>.
    /// Проверка запускается при открытии и идёт без блокировки интерфейса.
    /// </summary>
    public partial class AuthenticityWindow : Window
    {
        private readonly string? _clientPath;
        private readonly CancellationTokenSource _cts = new();
        private AuthenticityReport? _report;

        internal AuthenticityWindow(string? clientPath)
        {
            InitializeComponent();
            _clientPath = clientPath;
            Loaded += (_, _) => Run();
            Closed += (_, _) => _cts.Cancel();
        }

        private async void Run()
        {
            btnRerun.IsEnabled = btnCopy.IsEnabled = btnVirusTotal.IsEnabled = false;
            txtVerdict.Text = "Проверка…";
            txtVerdict.Foreground = (Brush)FindResource("TextPrimary");
            txtReport.Text = "";
            try
            {
                _report = await AuthenticityChecker.RunAsync(_clientPath, _cts.Token);
                txtReport.Text = AuthenticityChecker.Format(_report);
                bool mismatch = AuthenticityChecker.HasMismatch(_report);
                txtVerdict.Text = mismatch
                    ? "Найдено несовпадение хеша — подробности ниже."
                    : $"Лаунчер: {_report.Launcher.Verdict}";
                txtVerdict.Foreground = (Brush)FindResource(mismatch ? "StatusDanger" : "TextPrimary");
                btnVirusTotal.IsEnabled = _report.Launcher.LocalSha256 != null;
                btnCopy.IsEnabled = true;
                LauncherLog.Write($"[Подлинность] {(mismatch ? "есть несовпадение" : _report.Launcher.Verdict)}");
            }
            catch (OperationCanceledException)
            {
                // Окно закрыто посреди проверки.
            }
            catch (Exception ex)
            {
                txtVerdict.Text = $"Проверка не выполнена: {ex.Message}";
                LauncherLog.Write($"[Подлинность] Проверка не выполнена: {ex}");
            }
            finally
            {
                if (!_cts.IsCancellationRequested) btnRerun.IsEnabled = true;
            }
        }

        private void BtnRelease_Click(object sender, RoutedEventArgs e) =>
            SafeLinkOpener.Open($"https://github.com/Ven4ru/Ven4Tools/releases/tag/launcher-v{LauncherInstallation.GetCurrentVersion()}");

        private void BtnVirusTotal_Click(object sender, RoutedEventArgs e)
        {
            if (_report?.Launcher.LocalSha256 is { } sha)
                SafeLinkOpener.Open($"https://www.virustotal.com/gui/file/{sha}");
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(txtReport.Text); }
            catch (Exception ex) { LauncherLog.Write($"[Буфер обмена] Не удалось скопировать: {ex.Message}"); }
        }

        private void BtnRerun_Click(object sender, RoutedEventArgs e) => Run();

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
