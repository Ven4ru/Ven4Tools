using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;
using Ven4Tools.Shared;

namespace Ven4Tools.Launcher
{
    public partial class MainWindow
    {
        // Единый сценарий для установщиков-одиночек Microsoft (WebView2, VC++):
        // потоковое скачивание с доверенного хоста → проверка подписи Microsoft под
        // удерживаемым FileShare.Read-хендлом (защита от TOCTOU) → запуск с точечной
        // элевацией через UAC при отсутствии прав администратора. Winget использует
        // те же строительные блоки (DownloadTrustedFileAsync), но ставится отдельным
        // multi-package PowerShell-скриптом, поэтому идёт своим путём.
        private async Task DownloadVerifyAndRunElevatedAsync(
            string url, string fileName, string args, string label, CancellationToken ct)
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"ven4_{Guid.NewGuid():N}_{fileName}");
            AddLog($"⬇️ Скачивание {label}...");
            // L4: раньше отменить зависшую загрузку/установку WebView2/VC++ было нечем —
            // кнопка «Отмена» показывалась только для скачивания клиента. Переиспользуем
            // ту же кнопку и CTS-поле, связав его с переданным таймаут-токеном.
            _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ct = _downloadCts.Token;
            Dispatcher.Invoke(() =>
            {
                progressDownload.Value = 0;
                txtDownloadStatus.Text = $"{label}: скачивание...";
                btnLaunchApp.IsEnabled = false;
                btnCancelDownload.Visibility = Visibility.Visible;
            });
            try
            {
                await DownloadTrustedFileAsync(url, tempFile, label, reportProgress: true, ct);

                // Скачано с доверенного хоста Microsoft по HTTPS, но перед запуском с
                // повышением прав дополнительно подтверждаем подпись Microsoft
                // (допускает штатное обновление содержимого по URL). FileShare.Read
                // держим открытым от проверки до запуска: запрещает подмену файла
                // другим процессом того же пользователя в этом окне.
                using (new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (!AuthenticodeVerifier.IsSignedByMicrosoft(tempFile, out string sigError))
                    {
                        AddLog($"⛔ Подлинность установщика {label} не подтверждена ({sigError}) — установка отменена");
                        Dispatcher.Invoke(() => txtDownloadStatus.Text = "Подлинность не подтверждена");
                        return;
                    }
                    AddLog($"✅ Подпись Microsoft подтверждена ({label})");

                    AddLog($"📦 Установка {label}...");
                    Dispatcher.Invoke(() => txtDownloadStatus.Text = $"{label}: установка...");
                    // Без прав администратора — точечная элевация через UAC (Verb = runas)
                    bool needElevation = !IsRunAsAdmin();
                    var psi = new ProcessStartInfo
                    {
                        FileName = tempFile, Arguments = args,
                        UseShellExecute = needElevation, CreateNoWindow = !needElevation
                    };
                    if (needElevation) psi.Verb = "runas";
                    using var proc = Process.Start(psi);
                    // ct — тот же бюджет времени (5 минут / кнопка «Отмена»), что и у
                    // скачивания выше. Раньше ожидание установщика не принимало токен:
                    // зависший WebView2/VC++-инсталлятор не давал «Отмене» выхода —
                    // кнопка оставалась видимой, но бездействовала (тот же пробел, что
                    // был у RunWingetInstallScriptAsync). Процесс не убивается
                    // принудительно при отмене — тот же осознанный выбор, что и в
                    // InstallChocoAsync/RunWingetInstallScriptAsync.
                    if (proc != null) await proc.WaitForExitAsync(ct);
                }
                // Завершение процесса установщика ≠ успех: exit code Microsoft-установщиков
                // ненадёжен, а WebView2/VC++ могут «встать» только после перезагрузки.
                // Единственный источник правды об успехе — перепроверка через
                // IsWebView2Installed()/IsVcRedistInstalled() у вызывающего кода
                // (CheckComponentsInteractiveAsync), поэтому здесь успех НЕ логируем,
                // чтобы не было противоречия «✅ установлен» + «⚠️ не обнаружен».
                Dispatcher.Invoke(() => { progressDownload.Value = 100; txtDownloadStatus.Text = $"{label}: установка завершена"; });
            }
            catch (OperationCanceledException)
            {
                AddLog($"⏹ Установка {label} отменена");
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "Отменено");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка установки {label}: {ex.Message}");
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "Ошибка");
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                _downloadCts?.Dispose();
                _downloadCts = null;
                Dispatcher.Invoke(() =>
                {
                    progressDownload.Value = 0;
                    btnLaunchApp.IsEnabled = true;
                    btnCancelDownload.Visibility = Visibility.Collapsed;
                    btnCancelDownload.IsEnabled = true;
                });
            }
        }

        private static bool IsWebView2Installed()
        {
            string[] machinePaths = {
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
            };
            foreach (var p in machinePaths)
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(p);
                if (k?.GetValue("pv") is string v && !string.IsNullOrEmpty(v) && v != "0.0.0.0")
                    return true;
            }
            using var uk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
            if (uk?.GetValue("pv") is string uv && !string.IsNullOrEmpty(uv) && uv != "0.0.0.0")
                return true;
            return false;
        }

        private static bool IsVcRedistInstalled()
        {
            string[] keyPaths = {
                @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64",
                @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X64"
            };
            foreach (var p in keyPaths)
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(p);
                if (k?.GetValue("Installed") is int v && v == 1)
                    return true;
            }
            return false;
        }

        private async Task InstallWebView2Async()
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await DownloadVerifyAndRunElevatedAsync(
                "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                "MicrosoftEdgeWebview2Setup.exe",
                "/silent /install",
                "WebView2",
                timeoutCts.Token);
        }

        private async Task InstallVcRedistAsync()
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await DownloadVerifyAndRunElevatedAsync(
                "https://aka.ms/vs/17/release/vc_redist.x64.exe",
                "vc_redist.x64.exe",
                "/install /quiet /norestart",
                "VC++",
                timeoutCts.Token);
        }
    }
}
