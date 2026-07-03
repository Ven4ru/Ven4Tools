using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Ven4Tools.Launcher
{
    // Опциональные менеджеры пакетов (Chocolatey, Scoop) на экране предусловий.
    // В отличие от winget они НЕ обязательны для работы клиента — это дополнительные
    // источники установки приложений, поэтому их отсутствие ничего не блокирует.
    // Логика детекции и установки — своя копия по образцу PackageManagerService клиента
    // (общей библиотеки между клиентом и лаунчером нет намеренно).
    public partial class MainWindow
    {
        // ── Chocolatey ────────────────────────────────────────────────────────────

        private async Task<(bool IsInstalled, string? Version)> CheckChocoInstalledAsync()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = "choco.exe",
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
            Dispatcher.Invoke(() =>
            {
                progressDownload.Value = 0;
                txtDownloadStatus.Text = "Chocolatey: установка...";
                btnLaunchApp.IsEnabled = false;
            });

            // Официальный установочный скрипт Chocolatey (community.chocolatey.org).
            // Команда передаётся напрямую через -EncodedCommand (Base64 UTF-16LE),
            // без временного .ps1-файла: у файла в %TEMP% между записью и
            // elevated-запуском есть окно подмены содержимого (TOCTOU), а аргумент
            // командной строки фиксируется в момент старта процесса.
            const string installCommand =
                "Set-ExecutionPolicy Bypass -Scope Process -Force\r\n" +
                "[System.Net.ServicePointManager]::SecurityProtocol = " +
                "[System.Net.ServicePointManager]::SecurityProtocol -bor 3072\r\n" +
                "iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))\r\n";
            string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(installCommand));

            try
            {
                // Chocolatey ставится в C:\ProgramData — без прав администратора
                // используем точечную элевацию через UAC (Verb = runas)
                bool needElevation = !IsRunAsAdmin();
                var psi = new ProcessStartInfo
                {
                    FileName               = "powershell.exe",
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
                        await proc.WaitForExitAsync();
                        await stdoutTask;
                        if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                            AddLog($"⚠️ PowerShell: {stderr.Trim()}");
                    }
                    else
                        await proc.WaitForExitAsync();
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
            catch (Exception ex)
            {
                // Сюда попадает и отказ в запросе UAC — это не критично,
                // Chocolatey опционален
                AddLog($"❌ Ошибка установки Chocolatey: {ex.Message}");
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "Ошибка");
            }
            finally
            {
                Dispatcher.Invoke(() => { progressDownload.Value = 0; btnLaunchApp.IsEnabled = true; });
            }
        }

        // ── Scoop ─────────────────────────────────────────────────────────────────

        private async Task<bool> CheckScoopInstalledAsync()
        {
            // Основная проверка — шим-файл в профиле пользователя
            string shimPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "scoop", "shims", "scoop.cmd");
            if (File.Exists(shimPath)) return true;

            // Запасная проверка — вызов scoop help (нестандартный путь установки)
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = "scoop",
                    Arguments              = "help",
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };
                using var process = Process.Start(psi);
                if (process == null) return false;

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(timeoutCts.Token); }
                catch (OperationCanceledException)
                {
                    try { process.Kill(); } catch { }
                    return false;
                }

                await stdoutTask;
                await stderrTask;
                return process.ExitCode == 0;
            }
            catch { return false; }
        }

        private async Task InstallScoopAsync()
        {
            AddLog("📦 Установка Scoop...");
            Dispatcher.Invoke(() =>
            {
                progressDownload.Value = 0;
                txtDownloadStatus.Text = "Scoop: установка...";
                btnLaunchApp.IsEnabled = false;
            });

            // Официальный установочный скрипт Scoop (get.scoop.sh).
            // Scoop ставится в профиль пользователя, права администратора не нужны;
            // при запуске от администратора скрипту нужен явный параметр -RunAsAdmin.
            string installLine = IsRunAsAdmin()
                ? "iex \"& {$(Invoke-RestMethod -Uri https://get.scoop.sh)} -RunAsAdmin\""
                : "Invoke-RestMethod -Uri https://get.scoop.sh | Invoke-Expression";

            string tempScript = Path.Combine(Path.GetTempPath(), $"scoop_install_{Guid.NewGuid():N}.ps1");
            try
            {
                File.WriteAllText(tempScript,
                    "Set-ExecutionPolicy RemoteSigned -Scope CurrentUser -Force\r\n" +
                    installLine + "\r\n",
                    Encoding.UTF8);

                var psi = new ProcessStartInfo
                {
                    FileName               = "powershell.exe",
                    Arguments              = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\"",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                    string stderr  = await proc.StandardError.ReadToEndAsync();
                    await proc.WaitForExitAsync();
                    await stdoutTask;
                    if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                        AddLog($"⚠️ PowerShell: {stderr.Trim()}");
                }

                bool installed = await CheckScoopInstalledAsync();
                if (installed)
                {
                    AddLog("✅ Scoop успешно установлен");
                    Dispatcher.Invoke(() => txtDownloadStatus.Text = "Scoop установлен");
                }
                else
                {
                    AddLog("⚠️ Scoop не найден после установки. Возможно, требуется перезапуск лаунчера.");
                    Dispatcher.Invoke(() => txtDownloadStatus.Text = "Scoop: не найден после установки");
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка установки Scoop: {ex.Message}");
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "Ошибка");
            }
            finally
            {
                try { File.Delete(tempScript); } catch { }
                Dispatcher.Invoke(() => { progressDownload.Value = 0; btnLaunchApp.IsEnabled = true; });
            }
        }

        // Диалоги «Установить сейчас?» для опциональных менеджеров — вызываются из
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

            if (!await CheckScoopInstalledAsync())
            {
                var r = System.Windows.MessageBox.Show(
                    "Scoop не установлен.\n\n" +
                    "Это необязательный дополнительный источник установки приложений —\n" +
                    "клиент Ven4Tools полноценно работает и без него.\n\n" +
                    "Установить Scoop сейчас?",
                    "Scoop (опционально)", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r == MessageBoxResult.Yes)
                    await InstallScoopAsync();
            }
        }
    }
}
