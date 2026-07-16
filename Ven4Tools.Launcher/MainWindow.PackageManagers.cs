using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    // Опциональный менеджер пакетов (Chocolatey) на экране предусловий.
    // В отличие от winget он НЕ обязателен для работы клиента — это дополнительный
    // источник установки приложений, поэтому его отсутствие ничего не блокирует.
    // Логика детекции и установки — своя копия по образцу PackageManagerService клиента
    // (общей библиотеки между клиентом и лаунчером нет намеренно).
    public partial class MainWindow
    {
        private async Task ProcessSetupComponentRequestsAsync()
        {
            var requested = SetupComponentRequestService.Consume(
                AppDomain.CurrentDomain.BaseDirectory);
            if (requested.Count == 0)
                return;

            AddLog("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            AddLog("📦 Установка компонентов, выбранных в setup...");

            foreach (var component in requested)
            {
                switch (component)
                {
                    case SetupComponent.Winget:
                    {
                        var info = await CheckWingetWithVersionAsync();
                        if (info.IsInstalled)
                            AddLog($"✅ Winget {info.Version} уже установлен");
                        else
                            await InstallWingetAsync(interactive: false);
                        break;
                    }

                    case SetupComponent.Chocolatey:
                    {
                        var info = await CheckChocoInstalledAsync();
                        if (info.IsInstalled)
                            AddLog($"✅ Chocolatey {info.Version} уже установлен");
                        else
                            await InstallChocoAsync();
                        break;
                    }
                }
            }
        }

        // ── Chocolatey ────────────────────────────────────────────────────────────

        private async Task<(bool IsInstalled, string? Version)> CheckChocoInstalledAsync()
        {
            try
            {
                var chocoPath = Services.TrustedExecutablePaths.ResolveChocolatey();
                if (chocoPath == null) return (false, null);

                var psi = new ProcessStartInfo
                {
                    FileName               = chocoPath,
                    Arguments              = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };
                using var process = Process.Start(psi);
                if (process == null) return (false, null);

                // Читаем потоки в фоне — иначе дедлок, если буфер переполнится
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(timeoutCts.Token); }
                catch (OperationCanceledException)
                {
                    try { process.Kill(); } catch { }
                    return (false, null);
                }

                string output = await stdoutTask;
                await stderrTask;

                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                    return (false, null);

                return (true, output.Trim());
            }
            catch { return (false, null); }
        }

        private async Task InstallChocoAsync()
        {
            AddLog("📦 Установка Chocolatey...");
            // L4: как и для winget/WebView2/VC++, даём возможность прервать зависшую
            // установку — переиспользуем ту же кнопку и CTS-поле.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
            var ct = _downloadCts.Token;
            Dispatcher.Invoke(() =>
            {
                progressDownload.Value = 0;
                txtDownloadStatus.Text = "Chocolatey: установка...";
                btnLaunchApp.IsEnabled = false;
                btnCancelDownload.Visibility = Visibility.Visible;
            });

            // Официальный установочный скрипт Chocolatey. URL захардкожен как
            // HTTPS-литерал на конкретный домен community.chocolatey.org (нет
            // пользовательского ввода — валидировать хост/схему нечего), TLS 1.2
            // принудительно включён ниже (SecurityProtocol -bor 3072). Хеш скрипта
            // намеренно НЕ пиннится: это официальный upstream-механизм, скрипт на
            // стороне Chocolatey регулярно меняется, пиннинг сломал бы установку.
            // Ради прозрачности логируем ровно то, что будет скачано и исполнено.
            const string ChocoInstallScriptUrl = "https://community.chocolatey.org/install.ps1";
            AddLog($"⤓ Источник (iex): {ChocoInstallScriptUrl}");
            // Команда передаётся напрямую через -EncodedCommand (Base64 UTF-16LE),
            // без временного .ps1-файла: у файла в %TEMP% между записью и
            // elevated-запуском есть окно подмены содержимого (TOCTOU), а аргумент
            // командной строки фиксируется в момент старта процесса.
            string installCommand =
                "Set-ExecutionPolicy Bypass -Scope Process -Force\r\n" +
                "[System.Net.ServicePointManager]::SecurityProtocol = " +
                "[System.Net.ServicePointManager]::SecurityProtocol -bor 3072\r\n" +
                $"iex ((New-Object System.Net.WebClient).DownloadString('{ChocoInstallScriptUrl}'))\r\n";
            string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(installCommand));

            try
            {
                // Chocolatey ставится в C:\ProgramData — без прав администратора
                // используем точечную элевацию через UAC (Verb = runas)
                bool needElevation = !IsRunAsAdmin();
                var psi = new ProcessStartInfo
                {
                    FileName               = Services.TrustedExecutablePaths.PowerShellExe,
                    Arguments              = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
                    UseShellExecute        = needElevation,
                    RedirectStandardOutput = !needElevation,
                    RedirectStandardError  = !needElevation,
                    CreateNoWindow         = !needElevation
                };
                if (needElevation) psi.Verb = "runas";

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    if (!needElevation)
                    {
                        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                        string stderr  = await proc.StandardError.ReadToEndAsync();
                        await proc.WaitForExitAsync(ct);
                        await stdoutTask;
                        if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                            AddLog($"⚠️ PowerShell: {stderr.Trim()}");
                    }
                    else
                        await proc.WaitForExitAsync(ct);
                }

                var result = await CheckChocoInstalledAsync();
                if (result.IsInstalled)
                {
                    AddLog($"✅ Chocolatey {result.Version} успешно установлен");
                    Dispatcher.Invoke(() => txtDownloadStatus.Text = "Chocolatey установлен");
                }
                else
                {
                    AddLog("⚠️ Chocolatey не найден после установки. Возможно, требуется перезапуск лаунчера.");
                    Dispatcher.Invoke(() => txtDownloadStatus.Text = "Chocolatey: не найден после установки");
                }
            }
            catch (OperationCanceledException)
            {
                AddLog("⏹ Установка Chocolatey отменена");
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "Отменено");
            }
            catch (Exception ex)
            {
                // Сюда попадает и отказ в запросе UAC — это не критично,
                // Chocolatey опционален
                AddLog($"❌ Ошибка установки Chocolatey: {ex.Message}");
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "Ошибка");
            }
            finally
            {
                _downloadCts?.Dispose();
                _downloadCts = null;
                Dispatcher.Invoke(() =>
                {
                    progressDownload.Value = 0;
                    btnLaunchApp.IsEnabled = true;
                    btnCancelDownload.Visibility = Visibility.Collapsed;
                });
            }
        }

        // Диалог «Установить сейчас?» для опционального менеджера — вызывается из
        // интерактивной проверки компонентов. Не навязываемся: отказ ничего не блокирует.
        private async Task OfferOptionalPackageManagersAsync()
        {
            var chocoInfo = await CheckChocoInstalledAsync();
            if (!chocoInfo.IsInstalled)
            {
                var r = System.Windows.MessageBox.Show(
                    "Chocolatey не установлен.\n\n" +
                    "Это необязательный дополнительный источник установки приложений —\n" +
                    "клиент Ven4Tools полноценно работает и без него.\n\n" +
                    "Установить Chocolatey сейчас?",
                    "Chocolatey (опционально)", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r == MessageBoxResult.Yes)
                    await InstallChocoAsync();
            }
        }
    }
}
