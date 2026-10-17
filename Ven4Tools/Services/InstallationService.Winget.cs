using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public partial class InstallationService
    {
        // ── Источник: Winget ───────────────────────────────────────────────────
        private async Task<SourceAttempt> InstallFromWingetAsync(
            AppInfo app, string primaryId, string[] wingetSources, AppInstallProgress appProgress,
            IProgress<AppInstallProgress> progress, string installDrive, string? version,
            string outcomeCheckId, InstalledBaseline baseline, CancellationToken token)
        {
            if (string.IsNullOrEmpty(primaryId) || primaryId.StartsWith("User.")) return SourceAttempt.Failed("в каталоге нет пакета");
            // У записи каталога без wingetId (ExitLag, VK Play, драйверпаки…) primaryId —
            // внутренний ID каталога: `winget install exitlag` по обоим источникам заведомо
            // ничего не найдёт и только тратит время. Пользовательские приложения
            // (IsUserAdded) сюда не попадают: у них ID и есть winget-ID.
            if (string.IsNullOrEmpty(app.AlternativeId) && !app.IsUserAdded)
            {
                Log($"⏭ Winget: у «{app.DisplayName}» нет ID пакета winget — источник пропущен");
                return SourceAttempt.Failed("в каталоге нет пакета");
            }
            string? lastFailureDetail = null;
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

                // Сначала установщик «для всех пользователей». Без --scope winget сам выбирал
                // установщик для профиля пользователя, если в манифесте есть оба (FreeCAD,
                // Cursor, VS Code, Git — около 40 программ каталога): клиент работает от имени
                // администратора, и такая программа попадала в его профиль, а удалить её потом
                // можно было только без прав администратора. У пакета только с пользовательским
                // установщиком winget ответит «нет подходящего установщика» — тогда повтор без
                // --scope. Магазин Microsoft области установки не знает.
                string? scope = wsrc.Equals("msstore", StringComparison.OrdinalIgnoreCase) ? null : "machine";
                var wingetRun = await RunWingetAsync(primaryId, wsrc, token, version, installDrive, scope: scope);
                if (!wingetRun.Ok && scope != null && wingetRun.ExitCode == WingetErrorMapper.NoApplicableInstaller)
                {
                    Log($"🔁 Winget ({wsrc}, {primaryId}): установщика для всех пользователей нет — повтор без выбора области");
                    scope = null;
                    wingetRun = await RunWingetAsync(primaryId, wsrc, token, version, installDrive);
                }

                // Часть пакетов (Battle.net) winget отказывается ставить без явной папки:
                // «Install location is required by the package». Повторяем один раз
                // с папкой «Program Files\<название>» на выбранном (или системном) диске.
                if (!wingetRun.Ok && wingetRun.ExitCode == WingetErrorMapper.InstallLocationRequired)
                {
                    string drive = IsNonSystemDrive(installDrive) ? installDrive : GetSystemDriveRoot();
                    string folder = TargetFolderFor(drive, app.DisplayName);
                    if (CommandLineGuard.ValidateInstallFolder(folder))
                    {
                        Log($"🔁 Winget ({wsrc}, {primaryId}): пакет требует папку установки — повтор с {folder}");
                        wingetRun = await RunWingetAsync(primaryId, wsrc, token, version, installDrive, folder, scope);
                    }
                }

                if (wingetRun.Ok)
                    return SourceAttempt.Finished(await ReportInstallOutcomeAsync(
                        app, appProgress, progress, outcomeCheckId, baseline,
                        true, wingetRun.Reboot, "winget", token, sourceDetail: wsrc));

                // Установщик отказывается работать от имени администратора — повторяем тот же
                // winget без повышения прав. Код выхода такого запуска не виден, поэтому
                // итог решает проверка по факту.
                if (wingetRun.ExitCode == WingetErrorMapper.InstallerProhibitsElevation &&
                    await RunWingetUnelevatedAsync(primaryId, wsrc, version, token))
                    return SourceAttempt.Finished(await ReportInstallOutcomeAsync(
                        app, appProgress, progress, outcomeCheckId, baseline,
                        false, false, "winget", token,
                        WingetErrorMapper.MapExitCode(wingetRun.ExitCode), sourceDetail: wsrc));

                switch (WingetErrorMapper.ClassifyInstallFailure(wingetRun.ExitCode))
                {
                    case WingetFailureAction.AlreadyInstalled:
                        Log($"ℹ️ Winget ({wsrc}, {primaryId}): {WingetErrorMapper.MapExitCode(wingetRun.ExitCode)}");
                        return SourceAttempt.Finished(await ReportInstallOutcomeAsync(
                            app, appProgress, progress, outcomeCheckId, baseline,
                            true, false, "winget", token, sourceDetail: wsrc));
                    case WingetFailureAction.Stop:
                        string stopDetail = WingetErrorMapper.MapExitCode(wingetRun.ExitCode);
                        Log($"❌ Winget ({wsrc}, {primaryId}): {stopDetail} — другие источники не пробуем, причина у них та же");
                        return SourceAttempt.Finished(await ReportInstallOutcomeAsync(
                            app, appProgress, progress, outcomeCheckId, baseline,
                            false, false, "winget", token, stopDetail, wsrc));
                }

                // Код выхода больше не теряется на границе метода: расшифровываем его
                // в читаемую причину — и в лог, и дальше в блок «Не установлено».
                // -1 — синтетический признак «winget вообще не запускался» (не найден,
                // недопустимая версия): расшифровывать нечего, писать пользователю
                // «winget завершился с кодом -1» было бы прямой неправдой — причина
                // такого источника уже отдельно записана в лог выше по стеку.
                if (wingetRun.ExitCode == -1) continue;
                lastFailureDetail = WingetErrorMapper.MapExitCode(wingetRun.ExitCode);
                Log($"❌ Winget ({wsrc}, {primaryId}): {lastFailureDetail}");
            }
            return SourceAttempt.Failed(lastFailureDetail);
        }

        // Сторож winget: без него одно зависшее скачивание навсегда останавливало
        // весь набор (стенд, 16.10.2026). Признак работы — ввод-вывод winget и его
        // потомков плюс рост папки загрузки %TEMP%\WinGet (скачивание через службу
        // Delivery Optimization пишет туда, но не из дерева winget).
        internal static readonly TimeSpan WingetIdleLimit = TimeSpan.FromMinutes(15);
        internal static readonly TimeSpan WingetHardLimit = TimeSpan.FromMinutes(90);
        private static readonly TimeSpan WingetSampleInterval = TimeSpan.FromSeconds(30);

        /// <returns>null — winget завершился сам; иначе причина, по которой он прерван.</returns>
        private static async Task<string?> WaitForWingetAsync(Process process, CancellationToken token)
        {
            var detector = new InstallStallDetector(DateTime.UtcNow, WingetIdleLimit, WingetHardLimit);
            string downloads = Path.Combine(Path.GetTempPath(), "WinGet");
            var nextSample = DateTime.UtcNow;
            while (!process.HasExited)
            {
                // Та же схема, что WaitForExitRespectingCancellationAsync: пауза без токена,
                // иначе отмена вылетала бы из паузы мимо Kill.
                if (token.IsCancellationRequested)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    token.ThrowIfCancellationRequested();
                }
                if (DateTime.UtcNow >= nextSample)
                {
                    nextSample = DateTime.UtcNow + WingetSampleInterval;
                    long activity = await Task.Run(() => ProcessActivityProbe.Sample(process.Id, downloads));
                    string? reason = detector.Observe(DateTime.UtcNow, activity);
                    if (reason != null && !process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); } catch { }
                        return reason;
                    }
                }
                await Task.Delay(100, CancellationToken.None);
            }
            return null;
        }

        /// <summary>
        /// Повтор установки через winget без прав администратора — для установщиков,
        /// которые от имени администратора не работают (0x8A150056). Ждёт, пока запущенный
        /// winget завершится, но не дольше общего предела установки.
        /// </summary>
        /// <returns>false — запустить winget без прав администратора не удалось.</returns>
        private async Task<bool> RunWingetUnelevatedAsync(string appId, string source, string? version, CancellationToken token)
        {
            if (!string.IsNullOrEmpty(version) && !CommandLineGuard.ValidateId(version)) return false;
            var wingetExe = TrustedExecutablePaths.ResolveWinget();
            if (wingetExe == null) return false;
            var args = new System.Collections.Generic.List<string> { "install", "--id", appId, "-e", "--source", source };
            if (!string.IsNullOrEmpty(version)) { args.Add("--version"); args.Add(version); }
            args.AddRange(WingetArgs.Modify());
            if (ProfileService.Current.SilentInstall) args.Add("--silent");

            var startedUtc = DateTime.UtcNow;
            if (!UnelevatedLauncher.TryStart(wingetExe, string.Join(" ", args.Select(AppUninstallService.QuoteArg))))
                return false;
            Log($"🔁 Winget ({source}, {appId}): установщик не работает от имени администратора — повтор без повышения прав");

            await Task.Delay(3000, CancellationToken.None); // оболочке нужно время, чтобы процесс появился
            var deadline = DateTime.UtcNow + WingetHardLimit;
            while (DateTime.UtcNow < deadline && AppUninstallService.CountStartedSince("winget", null, startedUtc, kill: false) > 0)
            {
                if (token.IsCancellationRequested)
                {
                    AppUninstallService.CountStartedSince("winget", null, startedUtc, kill: true);
                    token.ThrowIfCancellationRequested();
                }
                await Task.Delay(1000, CancellationToken.None);
            }
            int killed = AppUninstallService.CountStartedSince("winget", null, startedUtc, kill: true);
            if (killed > 0)
                Log($"⏱️ Winget [{appId}]: установка без прав администратора не завершилась за {WingetHardLimit.TotalMinutes:0} мин — процесс прерван");
            return true;
        }

        // Возвращает признак перезагрузки отдельно от общего Ok (0 и 3010 — оба
        // Ok=true) — раньше это различие терялось на возврате из метода, и путь
        // Winget никогда не мог показать «Требуется перезагрузка» в отличие от
        // остальных путей (RunElevatedInstallerAsync возвращает Reboot честно).
        // Код выхода отдаётся третьим полем — по нему WingetErrorMapper строит
        // человекочитаемую причину неудачи (-1 = winget вообще не запустился).
        private async Task<(bool Ok, bool Reboot, int ExitCode)> RunWingetAsync(string appId, string source, CancellationToken token, string? version = null, string? installDrive = null, string? requiredLocation = null, string? scope = null)
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

            // Повтор для пакета, которому winget обязательно нужна папка (см. вызов выше).
            if (location == null && requiredLocation != null)
                location = requiredLocation;

            // msstore/MSIX игнорируют --location или завершаются с ошибкой
            if (source.Equals("msstore", StringComparison.OrdinalIgnoreCase))
                location = null;

            // Версия приходит из внешнего каталога или выбора пользователя —
            // валидируем перед подстановкой в командную строку (паритет с appId).
            if (!string.IsNullOrEmpty(version) && !CommandLineGuard.ValidateId(version))
            {
                Log($"❌ Недопустимая версия «{version}» для {appId} — источник Winget пропущен");
                return (false, false, -1);
            }

            var wingetExe = TrustedExecutablePaths.ResolveWinget();
            if (wingetExe == null)
            {
                Log($"❌ winget не найден по доверенному пути для {appId}");
                return (false, false, -1);
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
            if (scope != null)
            {
                psi.ArgumentList.Add("--scope");
                psi.ArgumentList.Add(scope);
            }
            foreach (var flag in WingetArgs.Modify()) psi.ArgumentList.Add(flag);
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
                    return (false, false, -1);
                }
                catch (FileNotFoundException)
                {
                    Log($"❌ winget не найден — источник Winget пропущен ({appId})");
                    return (false, false, -1);
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                AppLogger.Write($"▶ Запущен процесс установки PID {process.Id}: winget install {appId}");

                // Общий хелпер (см. InstallationService.cs) — killает всё дерево процессов
                // при отмене вместо того, чтобы выбросить исключение прямо из паузы.
                string? stalled = await WaitForWingetAsync(process, token);
                if (stalled != null)
                {
                    Log($"⏱️ Winget [{appId}]: {stalled} — процесс прерван");
                    return (false, false, WingetErrorMapper.StalledByClient);
                }

                // 3010 и коды winget «перезагрузите для завершения» — установка прошла успешно.
                // Раньше успехом считался только 3010, и winget-код перезагрузки уводил
                // цепочку на следующий источник — повторная установка того же приложения.
                bool reboot = WingetErrorMapper.IsSuccessWithReboot(process.ExitCode);
                if (reboot)
                    Log($"⚠ Установлено. Требуется перезагрузка. ({appId})");
                return (process.ExitCode == 0 || reboot, reboot, process.ExitCode);
            }
        }
    }
}
