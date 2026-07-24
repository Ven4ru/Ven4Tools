using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Helpers;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Services
{
    public partial class InstallationService
    {
        // ── Источник: Winget ───────────────────────────────────────────────────
        private async Task<(bool Success, string Message, AppInstallProgress Progress)?> InstallFromWingetAsync(
            AppInfo app, string primaryId, string[] wingetSources, AppInstallProgress appProgress,
            IProgress<AppInstallProgress> progress, string installDrive, string? version,
            string outcomeCheckId, InstalledBaseline baseline, CancellationToken token)
        {
            if (string.IsNullOrEmpty(primaryId) || primaryId.StartsWith("User.")) return null;
            foreach (var wsrc in wingetSources)
            {
                token.ThrowIfCancellationRequested();
                // Winget скачивает и ставит пакет как единый чёрный ящик: RunWingetAsync
                // только логирует построчный вывод, не парсит из него проценты (WingetRunner
                // сознательно отбрасывает строки прогресс-бара как шум, см. IsTableSeparator/
                // построчный фильтр). Локализованный вывод (без --locale en-US по правилам
                // проекта) делает матчинг "Downloading"/"Installing" по тексту хрупким —
                // поэтому честно показываем IsIndeterminate на весь процесс, а не
                // выдумываем разбивку на фазы, которой на самом деле не видно.
                appProgress.Status = $"📦 Winget ({wsrc})...";
                appProgress.Phase = InstallPhase.Installing;
                appProgress.IsIndeterminate = true;
                appProgress.Percentage = 0;
                progress.Report(appProgress);

                var wingetRun = await RunWingetAsync(primaryId, wsrc, token, version, installDrive);
                if (wingetRun.Ok)
                    return await ReportInstallOutcomeAsync(app, appProgress, progress, outcomeCheckId, baseline,
                        true, wingetRun.Reboot, "winget", $"Winget ({wsrc})", token);
            }
            return null;
        }

        // Возвращает признак перезагрузки отдельно от общего Ok (0 и 3010 — оба
        // Ok=true) — раньше это различие терялось на возврате из метода, и путь
        // Winget никогда не мог показать «Требуется перезагрузка» в отличие от
        // остальных путей (RunElevatedInstallerAsync возвращает Reboot честно).
        private async Task<(bool Ok, bool Reboot)> RunWingetAsync(string appId, string source, CancellationToken token, string? version = null, string? installDrive = null)
        {
            var profile = ProfileService.Current;

            // Применяем выбранный диск установки. Для несистемного диска —
            // «{диск}:\Program Files»; иначе используем DefaultInstallFolder из профиля.
            // Обе ветки теперь валидируются одинаково (раньше folderOnSameDrive
            // подставлял DefaultInstallFolder без CommandLineGuard.ValidateInstallFolder).
            string? location = null;
            if (IsNonSystemDrive(installDrive))
            {
                string driveUpper = installDrive!.TrimEnd('\\', '/').ToUpperInvariant();
                bool folderOnSameDrive = !string.IsNullOrWhiteSpace(profile.DefaultInstallFolder) &&
                    profile.DefaultInstallFolder.TrimEnd('\\', '/').ToUpperInvariant().StartsWith(driveUpper) &&
                    CommandLineGuard.ValidateInstallFolder(profile.DefaultInstallFolder);
                location = folderOnSameDrive
                    ? profile.DefaultInstallFolder
                    : ProgramFilesOn(installDrive!);
            }
            else if (!string.IsNullOrWhiteSpace(profile.DefaultInstallFolder) && CommandLineGuard.ValidateInstallFolder(profile.DefaultInstallFolder))
                location = profile.DefaultInstallFolder;

            // msstore/MSIX игнорируют --location или завершаются с ошибкой
            if (source.Equals("msstore", StringComparison.OrdinalIgnoreCase))
                location = null;

            // Версия приходит из внешнего каталога или выбора пользователя —
            // валидируем перед подстановкой в командную строку (паритет с appId).
            if (!string.IsNullOrEmpty(version) && !CommandLineGuard.ValidateId(version))
            {
                Log($"❌ Недопустимая версия «{version}» для {appId} — источник Winget пропущен");
                return (false, false);
            }

            var wingetExe = TrustedExecutablePaths.ResolveWinget();
            if (wingetExe == null)
            {
                Log($"❌ winget не найден по доверенному пути для {appId}");
                return (false, false);
            }
            var psi = new ProcessStartInfo
            {
                FileName = wingetExe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            // Аргументы через ArgumentList — .NET сам экранирует каждый токен, устраняя
            // саму поверхность инъекции (в т.ч. для DefaultInstallFolder из profile.json,
            // который доступен на запись любому не-elevated процессу пользователя).
            psi.ArgumentList.Add("install");
            psi.ArgumentList.Add("--id");
            psi.ArgumentList.Add(appId);
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("--source");
            psi.ArgumentList.Add(source);
            if (!string.IsNullOrEmpty(version))
            {
                psi.ArgumentList.Add("--version");
                psi.ArgumentList.Add(version);
            }
            psi.ArgumentList.Add("--accept-package-agreements");
            psi.ArgumentList.Add("--accept-source-agreements");
            psi.ArgumentList.Add("--disable-interactivity");
            if (profile.SilentInstall) psi.ArgumentList.Add("--silent");
            if (location != null)
            {
                psi.ArgumentList.Add("--location");
                psi.ArgumentList.Add(location);
            }

            using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                process.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data) && !token.IsCancellationRequested)
                    {
                        Log($"Winget [{appId}]: {e.Data}");
                    }
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data) && !token.IsCancellationRequested)
                    {
                        Log($"Winget ERROR [{appId}]: {e.Data}");
                    }
                };

                try
                {
                    process.Start();
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // winget не установлен в системе — не валим весь цикл источников,
                    // просто сообщаем о неудаче, чтобы перейти к следующему источнику.
                    Log($"❌ winget не найден — источник Winget пропущен ({appId})");
                    return (false, false);
                }
                catch (FileNotFoundException)
                {
                    Log($"❌ winget не найден — источник Winget пропущен ({appId})");
                    return (false, false);
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                AppLogger.Write($"▶ Запущен процесс установки PID {process.Id}: winget install {appId}");

                while (!process.HasExited)
                {
                    if (token.IsCancellationRequested)
                    {
                        // Kill(true) — убиваем winget и все дочерние процессы (msiexec, setup.exe и др.)
                        try { process.Kill(entireProcessTree: true); } catch { }
                        token.ThrowIfCancellationRequested();
                    }
                    await Task.Delay(100, token);
                }

                // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED — установка прошла успешно
                bool reboot = process.ExitCode == 3010;
                if (reboot)
                    Log($"⚠ Установлено. Требуется перезагрузка. ({appId})");
                return (process.ExitCode == 0 || reboot, reboot);
            }
        }
    }
}
