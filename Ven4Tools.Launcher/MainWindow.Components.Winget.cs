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
        private async Task<(bool IsInstalled, string? Version, bool IsOutdated)> CheckWingetWithVersionAsync()
        {
            try
            {
                var wingetPath = Services.TrustedExecutablePaths.ResolveWinget();
                if (wingetPath == null) return (false, null, false);

                var psi = new ProcessStartInfo
                {
                    FileName               = wingetPath,
                    Arguments              = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };
                using var process = Process.Start(psi);
                if (process == null) return (false, null, false);

                var stderrTask = process.StandardError.ReadToEndAsync();
                var stdoutTask = process.StandardOutput.ReadToEndAsync();

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await process.WaitForExitAsync(timeoutCts.Token); }
                catch (OperationCanceledException)
                {
                    try { process.Kill(); } catch { }
                    return (false, null, false);
                }

                string output = await stdoutTask;
                await stderrTask;

                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                    return (false, null, false);

                string version = output.Trim().TrimStart('v');

                using var gitHubService   = new GitHubService();
                string? latestVersion     = await gitHubService.GetLatestWingetVersionAsync();
                bool isOutdated           = false;
                if (latestVersion != null && Version.TryParse(version, out var current) && Version.TryParse(latestVersion, out var latest))
                    isOutdated = current < latest;

                return (true, version, isOutdated);
            }
            catch { return (false, null, false); }
        }

        // interactive = false — автоматический (marker-driven) вызов из setup:
        // не показываем модальные диалоги и не предлагаем перезагрузку, чтобы
        // ничего не всплывало из скрытого/фонового окна (только запись в лог).
        private async Task InstallWingetAsync(bool interactive = true)
        {
            AddLog("📦 Получение информации о winget с GitHub...");

            string uniq       = Guid.NewGuid().ToString("N");
            string tempMsix   = Path.Combine(Path.GetTempPath(), $"ven4_{uniq}_winget_setup.msixbundle");
            string tempVcLibs = Path.Combine(Path.GetTempPath(), $"ven4_{uniq}_VCLibs.appx");
            string tempUiXaml = Path.Combine(Path.GetTempPath(), $"ven4_{uniq}_UIXaml.appx");

            Dispatcher.Invoke(() =>
            {
                progressDownload.Value    = 0;
                txtDownloadStatus.Text    = "Подготовка...";
                // L4: пользователь мог отменять только скачивание клиента — установка
                // компонентов (winget/WebView2/VC++) не давала прервать зависшую
                // загрузку/установку. Переиспользуем ту же кнопку и CTS-поле.
                btnCancelDownload.Visibility = interactive ? Visibility.Visible : Visibility.Collapsed;
                btnLaunchApp.IsEnabled    = false;
            });

            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                _downloadCts = interactive
                    ? CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token)
                    : null;
                var ct = interactive ? _downloadCts!.Token : timeoutCts.Token;

                string? msixUrl = await ResolveWingetMsixUrlAsync(ct);
                if (msixUrl == null) return;

                await DownloadWingetPackagesAsync(msixUrl, tempVcLibs, tempUiXaml, tempMsix, ct);

                if (!await RunWingetInstallScriptAsync(tempVcLibs, tempUiXaml, tempMsix, ct))
                    return;

                var result = await CheckWingetWithVersionAsync();
                if (result.IsInstalled)
                {
                    AddLog($"✅ Winget {result.Version} успешно установлен");
                    Dispatcher.Invoke(() => txtDownloadStatus.Text = "Winget установлен");
                }
                else
                {
                    AddLog("⚠️ Winget не найден после установки. Возможно, требуется перезагрузка.");
                    Dispatcher.Invoke(() => txtDownloadStatus.Text = "Требуется перезагрузка");

                    if (interactive)
                    {
                        var reboot = System.Windows.MessageBox.Show(
                            "Winget не обнаружен после установки.\n\nПерезагрузить компьютер сейчас?",
                            "Требуется перезагрузка", MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (reboot == MessageBoxResult.Yes)
                            Process.Start(new ProcessStartInfo(Services.TrustedExecutablePaths.ShutdownExe, "/r /t 10") { UseShellExecute = true });
                    }
                    else
                    {
                        // Автоматический вызов из setup: не показываем диалог и не
                        // предлагаем перезагрузку — только сообщаем в лог.
                        AddLog("ℹ️ Winget установлен из setup; для применения может потребоваться перезагрузка — выполните её вручную при необходимости.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                AddLog("⏹ Установка winget отменена");
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "Отменено");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка установки winget: {ex.Message}");
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "Ошибка");
            }
            finally
            {
                try { if (File.Exists(tempMsix))   File.Delete(tempMsix);   } catch { }
                try { if (File.Exists(tempVcLibs)) File.Delete(tempVcLibs); } catch { }
                try { if (File.Exists(tempUiXaml)) File.Delete(tempUiXaml); } catch { }
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

        // Поиск URL основного msixbundle winget (DesktopAppInstaller) в последнем
        // релизе microsoft/winget-cli с проверкой доверенности хоста. null —
        // ассет не найден или хост недоверенный (сообщение уже записано в лог).
        private async Task<string?> ResolveWingetMsixUrlAsync(CancellationToken ct)
        {
            var json    = await _httpClient.GetStringAsync("https://api.github.com/repos/microsoft/winget-cli/releases/latest", ct);
            var release = Newtonsoft.Json.Linq.JObject.Parse(json);

            string? msixUrl = release["assets"]?
                .FirstOrDefault(a => a["name"]?.ToString().EndsWith(".msixbundle") == true &&
                                     a["name"]?.ToString().Contains("DesktopAppInstaller") == true)?
                ["browser_download_url"]?.ToString();

            if (msixUrl == null) { AddLog("❌ Не удалось найти файл установки winget в последнем релизе"); return null; }

            // Защита от подмены: качаем только с доверенных доменов по HTTPS
            if (!DownloadValidator.IsAllowedDownloadHost(msixUrl))
            {
                AddLog($"⛔ Недоверенный URL загрузки winget — скачивание отменено: {msixUrl}");
                return null;
            }
            return msixUrl;
        }

        // Скачивание трёх пакетов winget: зависимости (VCLibs + UI.Xaml) параллельно
        // и без индивидуального прогресса (иначе две загрузки перебивали бы полосу
        // друг у друга), затем основной msixbundle — с прогрессом.
        private async Task DownloadWingetPackagesAsync(
            string msixUrl, string tempVcLibs, string tempUiXaml, string tempMsix, CancellationToken ct)
        {
            AddLog("⬇️ Скачивание зависимостей...");
            Dispatcher.Invoke(() => txtDownloadStatus.Text = "Скачивание зависимостей...");

            var vcLibsTask = DownloadTrustedFileAsync(
                "https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx", tempVcLibs, "VCLibs", reportProgress: false, ct);
            var uiXamlTask = DownloadTrustedFileAsync(
                "https://github.com/microsoft/microsoft-ui-xaml/releases/download/v2.8.6/Microsoft.UI.Xaml.2.8.x64.appx",
                tempUiXaml, "UI.Xaml", reportProgress: false, ct);

            await Task.WhenAll(vcLibsTask, uiXamlTask);

            AddLog($"⬇️ Скачивание winget ({msixUrl.Split('/').Last()})...");
            await DownloadTrustedFileAsync(msixUrl, tempMsix, "Winget", reportProgress: true, ct);
        }

        // Проверка подписи Microsoft у всех трёх пакетов и их установка одним
        // PowerShell-скриптом (Add-AppxPackage). Возвращает false, если хоть один
        // пакет не подписан Microsoft (установка отменена). FileShare.Read держим
        // открытым от проверки подписи до завершения Add-AppxPackage — запрещает
        // подмену файла другим процессом того же пользователя в этом окне (TOCTOU).
        //
        // ct — тот же бюджет времени (10 минут / кнопка «Отмена»), что и у скачивания
        // выше в InstallWingetAsync. Раньше ожидание PowerShell не принимало токен
        // вовсе: зависший Add-AppxPackage (или отклик пользователя на «Отмена» во
        // время установки) не имел никакого выхода — кнопка «Отмена» оставалась
        // видимой, но не действовала, пока PowerShell сам не завершится. Симметрично
        // уже исправленным CheckWingetWithVersionAsync (10 сек) и InstallChocoAsync
        // в MainWindow.PackageManagers.cs (тот же паттерн для родственной установки).
        // Как и в InstallChocoAsync, процесс не убивается принудительно при отмене —
        // Add-AppxPackage безопаснее довести до конца в фоне, чем прервать посреди
        // записи; отмена лишь освобождает UI-поток ожидания.
        private async Task<bool> RunWingetInstallScriptAsync(
            string tempVcLibs, string tempUiXaml, string tempMsix, CancellationToken ct)
        {
            using var vcLibsHandle = new FileStream(tempVcLibs, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var uiXamlHandle = new FileStream(tempUiXaml, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var msixHandle   = new FileStream(tempMsix,   FileMode.Open, FileAccess.Read, FileShare.Read);

            foreach (var (path, label) in new[] { (tempVcLibs, "VCLibs"), (tempUiXaml, "UI.Xaml"), (tempMsix, "winget") })
            {
                if (!AuthenticodeVerifier.IsSignedByMicrosoft(path, out string sigError))
                {
                    AddLog($"⛔ Подлинность пакета {label} не подтверждена ({sigError}) — установка отменена");
                    Dispatcher.Invoke(() => txtDownloadStatus.Text = "Подлинность не подтверждена");
                    return false;
                }
            }
            AddLog("✅ Подпись Microsoft подтверждена для всех пакетов");

            AddLog("📦 Установка winget...");
            Dispatcher.Invoke(() => txtDownloadStatus.Text = "Установка...");

            string tempScript = Path.Combine(Path.GetTempPath(), $"winget_install_{Guid.NewGuid():N}.ps1");
            try
            {
                // Одинарные кавычки в PowerShell отключают подстановку $-переменных в путях
                File.WriteAllText(tempScript,
                    $"$ErrorActionPreference = 'Stop'\r\n" +
                    $"try {{ Add-AppxPackage -Path '{tempVcLibs.Replace("'", "''")}' }} catch {{}}\r\n" +
                    $"try {{ Add-AppxPackage -Path '{tempUiXaml.Replace("'", "''")}' }} catch {{}}\r\n" +
                    $"Add-AppxPackage -Path '{tempMsix.Replace("'", "''")}' -ForceApplicationShutdown\r\n",
                    Encoding.UTF8);

                // Держим temp-скрипт открытым с FileShare.Read НЕПРЕРЫВНО от записи до
                // завершения PowerShell — иначе между закрытием файла записи и открытием
                // PowerShell остаётся окно для подмены содержимого другим процессом того
                // же пользователя (TOCTOU). Зеркалирует защиту InstallationService/пакетов
                // VCLibs-UI.Xaml-msix выше в этом же методе.
                using var scriptGuard = new FileStream(tempScript, FileMode.Open, FileAccess.Read, FileShare.Read);

                var psi = new ProcessStartInfo
                {
                    FileName               = Services.TrustedExecutablePaths.PowerShellExe,
                    Arguments              = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\"",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
                    string stderr  = await proc.StandardError.ReadToEndAsync(ct);
                    await proc.WaitForExitAsync(ct);
                    await stdoutTask;
                    if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                        AddLog($"⚠️ PowerShell: {stderr.Trim()}");
                }
            }
            finally
            {
                try { File.Delete(tempScript); } catch { }
            }
            return true;
        }
    }
}
