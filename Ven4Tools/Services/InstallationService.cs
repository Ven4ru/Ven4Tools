using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Services
{
    public class InstallationService : IDisposable
    {
        // Общий семафор установки на всё приложение. Установка запускается из трёх мест
        // (каталог, пины в MainWindow, переустановка из истории) — без единого ограничения
        // можно запустить параллельные msiexec, что вызывает ошибку Windows Installer 1618.
        public static readonly SemaphoreSlim InstallSemaphore = new SemaphoreSlim(1, 1);

        // Один общий HttpClient на приложение: пересоздание на каждый инстанс
        // приводит к socket exhaustion (рекомендация MS).
        // Без глобального таймаута: HttpClient.Timeout ограничивает всё тело ответа,
        // и загрузки больших установщиков (100+ МБ) обрывались через 30 секунд.
        // Таймаут на получение заголовков задаётся per-request через CancellationTokenSource.
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestHeaders = { { "User-Agent", "Ven4Tools" } }
        };
        private readonly string _logPath;
        private readonly object _logLock = new object();

        public InstallationService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logsFolder = Path.Combine(appData, "Ven4Tools", "logs");
            Directory.CreateDirectory(logsFolder);

            _logPath = Path.Combine(logsFolder, $"install_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
        }

        public async Task<(bool Success, string Message, AppInstallProgress Progress)> InstallAppAsync(
            AppInfo app, string[] wingetSources, CancellationToken token,
            IProgress<AppInstallProgress> progress, string installDrive, string? version = null,
            Func<string, Task<bool>>? confirmPmInstall = null)
        {
            var appProgress = new AppInstallProgress { AppId = app.Id, AppName = app.DisplayName };

            try
            {
                Log($"Начало установки: {app.DisplayName}");
                appProgress.Status = "Начинаем...";
                progress.Report(appProgress);

                token.ThrowIfCancellationRequested();

                // ── Local installer (drag-drop) ────────────────────────────────
                if (!string.IsNullOrEmpty(app.LocalInstallerPath) && File.Exists(app.LocalInstallerPath))
                {
                    appProgress.Status = "📂 Локальный установщик...";
                    appProgress.Percentage = 30;
                    progress.Report(appProgress);
                    Log($"📂 {app.DisplayName}: локальный файл {app.LocalInstallerPath}");

                    bool isMsi = app.LocalInstallerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
                    string locArgLocal = BuildInstallerLocationArg(isMsi, installDrive, app.DisplayName);
                    var psiLocal = new ProcessStartInfo
                    {
                        FileName        = isMsi ? "msiexec" : app.LocalInstallerPath,
                        Arguments       = isMsi
                                          ? $"/i \"{app.LocalInstallerPath}\" /quiet /norestart{locArgLocal}"
                                          : (string.IsNullOrWhiteSpace(app.SilentArgs) ? "/S" : app.SilentArgs) + locArgLocal,
                        UseShellExecute = true,
                        Verb            = "runas",
                        WindowStyle     = ProcessWindowStyle.Hidden
                    };
                    using var localProc = Process.Start(psiLocal);
                    if (localProc == null)
                    {
                        appProgress.Status = "❌ Не удалось запустить установщик";
                        progress.Report(appProgress);
                        Log($"❌ {app.DisplayName}: Process.Start вернул null");
                        InstallFailureService.Append(app.DisplayName, app.Id, "local", "Process.Start вернул null");
                        return (false, "Не удалось запустить установщик", appProgress);
                    }

                    while (!localProc.HasExited)
                    {
                        if (token.IsCancellationRequested) { try { localProc.Kill(entireProcessTree: true); } catch { } token.ThrowIfCancellationRequested(); }
                        await Task.Delay(100, token);
                    }

                    // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED — считаем успехом
                    if (localProc.ExitCode == 0 || localProc.ExitCode == 3010)
                    {
                        bool reboot = localProc.ExitCode == 3010;
                        appProgress.Status = reboot ? "⚠ Установлено. Требуется перезагрузка." : "✅ Установлено (локальный файл)";
                        appProgress.Percentage = 100;
                        progress.Report(appProgress);
                        Log(reboot
                            ? $"⚠ Установлено. Требуется перезагрузка. {app.DisplayName} — локальный установщик"
                            : $"✅ {app.DisplayName} — локальный установщик");
                        if (ProfileService.Current.SaveInstallHistory)
                            await InstallHistoryService.Instance.TrackAsync(app.Id, app.DisplayName, "local", app.CategoryString);
                        return (true, reboot ? "Установлено (требуется перезагрузка)" : "Установлено из локального файла", appProgress);
                    }

                    appProgress.Status = "❌ Ошибка локального установщика";
                    progress.Report(appProgress);
                    Log($"❌ {app.DisplayName}: локальный установщик завершился с кодом {localProc.ExitCode}");
                    InstallFailureService.Append(app.DisplayName, app.Id, "local", $"Код выхода {localProc.ExitCode}");
                    return (false, "Ошибка локального установщика", appProgress);
                }

                // Offline cache — try local installer first
                string? cachedPath = OfflineService.GetCachedInstallerPath(app.Id);
                if (cachedPath != null)
                {
                    if (!string.IsNullOrWhiteSpace(app.Sha256) &&
                        !await HashHelper.VerifyHashAsync(cachedPath, app.Sha256))
                    {
                        Log($"❌ SHA256 mismatch в кэше: {app.DisplayName}, удаляю");
                        try { File.Delete(cachedPath); } catch { }
                        cachedPath = null;
                    }
                }

                if (cachedPath != null)
                {
                    appProgress.Status = "🔌 Из кэша...";
                    appProgress.Percentage = 50;
                    progress.Report(appProgress);

                    bool cacheIsMsi = cachedPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
                    string locArgCache = BuildInstallerLocationArg(cacheIsMsi, installDrive, app.DisplayName);
                    var psiCache = new ProcessStartInfo
                    {
                        FileName       = cacheIsMsi ? "msiexec" : cachedPath,
                        Arguments      = cacheIsMsi
                                         ? $"/i \"{cachedPath}\" /quiet /norestart{locArgCache}"
                                         : "/S /silent /quiet" + locArgCache,
                        UseShellExecute = true, Verb = "runas",
                        WindowStyle     = ProcessWindowStyle.Hidden
                    };
                    using var cacheProc = Process.Start(psiCache);
                    if (cacheProc != null)
                    {
                        while (!cacheProc.HasExited)
                        {
                            if (token.IsCancellationRequested) { try { cacheProc.Kill(entireProcessTree: true); } catch { } token.ThrowIfCancellationRequested(); }
                            await Task.Delay(100, token);
                        }
                        // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED — считаем успехом
                        if (cacheProc.ExitCode == 0 || cacheProc.ExitCode == 3010)
                        {
                            bool reboot = cacheProc.ExitCode == 3010;
                            appProgress.Status = reboot ? "⚠ Установлено. Требуется перезагрузка." : "✅ Установлено (кэш)";
                            appProgress.Percentage = 100;
                            progress.Report(appProgress);
                            Log(reboot
                                ? $"⚠ Установлено. Требуется перезагрузка. {app.DisplayName} (офлайн-кэш)"
                                : $"✅ {app.DisplayName} установлено из офлайн-кэша");
                            return (true, reboot ? "Установлено из кэша (требуется перезагрузка)" : "Установлено из офлайн-кэша", appProgress);
                        }
                    }
                }

                // In strict offline mode with no cache — abort
                if (OfflineService.IsOffline)
                {
                    appProgress.Status = "❌ Нет в кэше (офлайн режим)";
                    progress.Report(appProgress);
                    Log($"⚠️ {app.DisplayName}: офлайн режим, установщик не кэширован");
                    return (false, "Офлайн режим — нет кэша", appProgress);
                }

                // ── Source-ordered install loop ────────────────────────────────
                string primaryId = !string.IsNullOrEmpty(app.AlternativeId) ? app.AlternativeId : app.Id;

                // ID может приходить из ручного ввода (пользовательские приложения,
                // альтернативные источники) — проверяем всегда перед подстановкой в
                // командную строку winget/choco/scoop, чтобы исключить внедрение аргументов.
                if (!CommandLineGuard.ValidateId(primaryId))
                {
                    appProgress.Status = "❌ Недопустимый идентификатор пакета";
                    progress.Report(appProgress);
                    Log($"❌ {app.DisplayName}: недопустимый ID «{primaryId}» — установка отменена");
                    InstallFailureService.Append(app.DisplayName, app.Id, "validation", $"Недопустимый ID «{primaryId}»");
                    return (false, "Недопустимый идентификатор пакета", appProgress);
                }

                var sourceOrder  = SourceOrderService.GetOrderForCategory(app.CategoryString);
                Log($"🔀 Порядок источников для «{app.DisplayName}»: {string.Join(" → ", sourceOrder)}");

                foreach (var srcId in sourceOrder)
                {
                    token.ThrowIfCancellationRequested();

                    switch (srcId)
                    {
                        // ── Winget ──────────────────────────────────────────────
                        case SourceOrderSettings.Winget:
                        {
                            if (string.IsNullOrEmpty(primaryId) || primaryId.StartsWith("User.")) break;
                            foreach (var wsrc in wingetSources)
                            {
                                token.ThrowIfCancellationRequested();
                                appProgress.Status = $"📦 Winget ({wsrc})...";
                                appProgress.Percentage = 10;
                                progress.Report(appProgress);

                                if (await RunWingetAsync(primaryId, wsrc, token, version, installDrive))
                                {
                                    appProgress.Status = "✅ Установлено (Winget)";
                                    appProgress.Percentage = 100;
                                    progress.Report(appProgress);
                                    Log($"✅ {app.DisplayName} — Winget ({wsrc}): {primaryId}");
                                    if (ProfileService.Current.SaveInstallHistory)
                                        await InstallHistoryService.Instance.TrackAsync(app.Id, app.DisplayName, "winget", app.CategoryString);
                                    return (true, "Установлено через Winget", appProgress);
                                }
                            }
                            break;
                        }

                        // ── Chocolatey ──────────────────────────────────────────
                        case SourceOrderSettings.Choco:
                        {
                            if (string.IsNullOrWhiteSpace(app.ChocoId)) break;
                            appProgress.Status = "🍫 Chocolatey...";
                            appProgress.Percentage = 15;
                            progress.Report(appProgress);

                            bool chocoOk = PackageManagerService.IsChocoInstalled()
                                || (!token.IsCancellationRequested
                                    && confirmPmInstall != null
                                    && await confirmPmInstall("Chocolatey")
                                    && await PackageManagerService.InstallChocoAsync(msg => Log(msg)));
                            if (chocoOk && await PackageManagerService.RunChocoInstallAsync(app.ChocoId, token, msg => Log(msg)))
                            {
                                appProgress.Status = "✅ Установлено (Chocolatey)";
                                appProgress.Percentage = 100;
                                progress.Report(appProgress);
                                Log($"✅ {app.DisplayName} — Chocolatey: {app.ChocoId}");
                                if (ProfileService.Current.SaveInstallHistory)
                                    await InstallHistoryService.Instance.TrackAsync(app.Id, app.DisplayName, "choco", app.CategoryString);
                                return (true, "Установлено через Chocolatey", appProgress);
                            }
                            break;
                        }

                        // ── Scoop ───────────────────────────────────────────────
                        case SourceOrderSettings.Scoop:
                        {
                            if (string.IsNullOrWhiteSpace(app.ScoopId)) break;
                            appProgress.Status = "🪣 Scoop...";
                            appProgress.Percentage = 18;
                            progress.Report(appProgress);

                            bool scoopOk = PackageManagerService.IsScoopInstalled()
                                || (!token.IsCancellationRequested
                                    && confirmPmInstall != null
                                    && await confirmPmInstall("Scoop")
                                    && await PackageManagerService.InstallScoopAsync(msg => Log(msg)));
                            if (scoopOk && await PackageManagerService.RunScoopInstallAsync(app.ScoopId, token, msg => Log(msg)))
                            {
                                appProgress.Status = "✅ Установлено (Scoop)";
                                appProgress.Percentage = 100;
                                progress.Report(appProgress);
                                Log($"✅ {app.DisplayName} — Scoop: {app.ScoopId}");
                                if (ProfileService.Current.SaveInstallHistory)
                                    await InstallHistoryService.Instance.TrackAsync(app.Id, app.DisplayName, "scoop", app.CategoryString);
                                return (true, "Установлено через Scoop", appProgress);
                            }
                            break;
                        }

                        // ── Direct download ─────────────────────────────────────
                        case SourceOrderSettings.Direct:
                        {
                            if (!app.InstallerUrls.Any()) break;

                            // Прямые ссылки без SHA256 в каталоге не выполняем:
                            // скачанный установщик нечем верифицировать, запускать его небезопасно.
                            if (!HashHelper.HasExpectedHash(app.Sha256))
                            {
                                appProgress.Status = "⚠ Нет SHA256 в каталоге — прямая ссылка пропущена";
                                progress.Report(appProgress);
                                Log($"⚠ Прямая ссылка без SHA256 для {app.DisplayName} — источник пропущен, пробую следующий");
                                AppLogger.Write($"[InstallationService] ⚠ Прямая ссылка без SHA256 для {app.DisplayName} — источник пропущен, продолжаю через winget");
                                InstallFailureService.Append(app.DisplayName, app.Id, "direct", "В каталоге не указан SHA256");
                                break;
                            }

                            foreach (var url in app.InstallerUrls)
                            {
                                token.ThrowIfCancellationRequested();

                                if (!DownloadValidator.ValidateUrl(url))
                                {
                                    AppLogger.Write($"[InstallationService] ⚠ Пропущен небезопасный URL (не HTTPS): {url}");
                                    continue;
                                }

                                appProgress.Status = "📥 Скачивание...";
                                appProgress.Percentage = 20;
                                progress.Report(appProgress);

                                string urlExt = Path.GetExtension(new Uri(url).LocalPath).ToLowerInvariant();
                                if (string.IsNullOrEmpty(urlExt) || (urlExt != ".exe" && urlExt != ".msi")) urlExt = ".exe";
                                string tempFile = Path.Combine(Path.GetTempPath(), $"{app.Id}_{Guid.NewGuid()}{urlExt}");
                                try
                                {
                                    // Таймаут 30 секунд только на установление соединения и заголовки;
                                    // скачивание тела ограничено лишь токеном отмены пользователя.
                                    using var headersCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                                    headersCts.CancelAfter(TimeSpan.FromSeconds(30));
                                    long totalRead = 0;
                                    using (var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headersCts.Token))
                                    {
                                        response.EnsureSuccessStatusCode();

                                        if (!DownloadValidator.ValidateAfterRedirect(response))
                                        {
                                            AppLogger.Write($"[InstallationService] ⚠ Редирект на небезопасный хост: {response.RequestMessage?.RequestUri?.Host}");
                                            throw new InvalidOperationException($"Редирект на небезопасный хост: {response.RequestMessage?.RequestUri?.Host}");
                                        }

                                        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                                        using var contentStream = await response.Content.ReadAsStreamAsync();
                                        using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);
                                        var buf = new byte[8192];
                                        int bytesRead;
                                        while ((bytesRead = await contentStream.ReadAsync(buf, token)) > 0)
                                        {
                                            token.ThrowIfCancellationRequested();
                                            await fileStream.WriteAsync(buf, 0, bytesRead, token);
                                            totalRead += bytesRead;
                                            if (totalBytes > 0)
                                            {
                                                appProgress.Percentage = 20 + (int)((double)totalRead / totalBytes * 30);
                                                progress.Report(appProgress);
                                            }
                                        }
                                    }

                                    double downloadedMb = Math.Round(totalRead / 1_048_576.0, 1);
                                    AppLogger.Write($"📥 Загружен установщик — {downloadedMb} МБ: {Path.GetFileName(tempFile)}");

                                    appProgress.Status = "🔐 Проверка SHA256...";
                                    appProgress.Percentage = 55;
                                    progress.Report(appProgress);

                                    // SHA256 гарантированно указан (проверено перед циклом) —
                                    // верификация выполняется всегда, без исключений.
                                    if (!await HashHelper.VerifyHashAsync(tempFile, app.Sha256!))
                                    {
                                        Log($"❌ SHA256 mismatch: {app.DisplayName}");
                                        try { File.Delete(tempFile); } catch { }
                                        appProgress.Status = "❌ Ошибка SHA256";
                                        progress.Report(appProgress);
                                        InstallFailureService.Append(app.DisplayName, app.Id, "direct", "SHA256 mismatch");
                                        return (false, "SHA256 mismatch", appProgress);
                                    }
                                    Log($"✅ SHA256 OK: {app.DisplayName}");

                                    token.ThrowIfCancellationRequested();
                                    appProgress.Status = "⚙️ Установка...";
                                    appProgress.Percentage = 60;
                                    progress.Report(appProgress);

                                    bool tempIsMsi = tempFile.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
                                    string silentArgs;
                                    if (tempIsMsi)
                                    {
                                        // MSI запускаем через msiexec в тихом режиме
                                        silentArgs = $"/i \"{tempFile}\" /quiet /norestart"
                                                     + BuildInstallerLocationArg(true, installDrive, app.DisplayName);
                                    }
                                    else
                                    {
                                        silentArgs = app.SilentArgs;
                                        if (!CommandLineGuard.ValidateSilentArgs(silentArgs))
                                        {
                                            AppLogger.Write($"[InstallationService] ⚠ SilentArgs содержит недопустимые символы для {app.DisplayName} — использую /S");
                                            silentArgs = "/S";
                                        }
                                        if (string.IsNullOrWhiteSpace(silentArgs) && ProfileService.Current.SilentInstall)
                                            silentArgs = "/S";
                                        // Best-effort путь установки для прямого EXE
                                        silentArgs += BuildInstallerLocationArg(false, installDrive, app.DisplayName);
                                    }

                                    var psi = new ProcessStartInfo
                                    {
                                        FileName = tempIsMsi ? "msiexec" : tempFile,
                                        Arguments = silentArgs,
                                        UseShellExecute = true, Verb = "runas",
                                        WindowStyle = ProcessWindowStyle.Hidden
                                    };
                                    using var proc = Process.Start(psi);
                                    if (proc != null)
                                    {
                                        AppLogger.Write($"▶ Запущен процесс установки PID {proc.Id}: {Path.GetFileName(psi.FileName)}");
                                        while (!proc.HasExited)
                                        {
                                            if (token.IsCancellationRequested) { try { proc.Kill(entireProcessTree: true); } catch { } token.ThrowIfCancellationRequested(); }
                                            await Task.Delay(100, token);
                                        }
                                        try { File.Delete(tempFile); } catch { }
                                        // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED — считаем успехом
                                        if (proc.ExitCode == 0 || proc.ExitCode == 3010)
                                        {
                                            bool reboot = proc.ExitCode == 3010;
                                            appProgress.Status = reboot ? "⚠ Установлено. Требуется перезагрузка." : "✅ Установлено (прямая ссылка)";
                                            appProgress.Percentage = 100;
                                            progress.Report(appProgress);
                                            Log(reboot
                                                ? $"⚠ Установлено. Требуется перезагрузка. {app.DisplayName} — прямая ссылка: {url}"
                                                : $"✅ {app.DisplayName} — прямая ссылка: {url}");
                                            if (ProfileService.Current.SaveInstallHistory)
                                                await InstallHistoryService.Instance.TrackAsync(app.Id, app.DisplayName, "direct", app.CategoryString);
                                            return (true, reboot ? "Установлено (требуется перезагрузка)" : "Установлено", appProgress);
                                        }
                                    }
                                }
                                // Отмена пользователем — пробрасываем; таймаут заголовков — пробуем следующий источник
                                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                                catch (Exception ex)
                                {
                                    Log($"❌ Прямая ссылка {url}: {ex.Message}");
                                    try { File.Delete(tempFile); } catch { }
                                }
                            }
                            break;
                        }
                    }
                }

                appProgress.Status = "❌ Ошибка";
                progress.Report(appProgress);
                Log($"❌ {app.DisplayName} — все источники исчерпаны");
                InstallFailureService.Append(app.DisplayName, app.Id, "all-sources", "Все источники исчерпаны");
                return (false, "Не удалось установить", appProgress);
            }
            catch (OperationCanceledException)
            {
                appProgress.Status = "⏹️ Отменено";
                progress.Report(appProgress);
                Log($"⏹️ {app.DisplayName} отменено");
                throw;
            }
            catch (Exception ex)
            {
                appProgress.Status = "❌ Ошибка";
                progress.Report(appProgress);
                Log($"❌ {app.DisplayName}: {ex.Message}");
                return (false, ex.Message, appProgress);
            }
        }

        // ── Помощники для выбора диска установки ───────────────────────────────

        private static string GetSystemDriveRoot()
            => Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

        /// <summary>
        /// true, если пользователь выбрал диск, отличный от системного.
        /// Только в этом случае имеет смысл переопределять путь установки —
        /// для системного диска поведение по умолчанию и так корректно.
        /// </summary>
        private static bool IsNonSystemDrive(string? installDrive)
        {
            if (string.IsNullOrWhiteSpace(installDrive)) return false;
            string sys = GetSystemDriveRoot().TrimEnd('\\', '/').ToUpperInvariant();
            string sel = installDrive.TrimEnd('\\', '/').ToUpperInvariant();
            return sel.Length >= 2 && sel != sys;
        }

        /// <summary>Базовая папка «{диск}:\Program Files».</summary>
        private static string ProgramFilesOn(string installDrive)
        {
            string root = installDrive.TrimEnd('\\', '/');
            return $"{root}\\Program Files";
        }

        /// <summary>Целевая папка установки для конкретного приложения (best-effort).</summary>
        private static string TargetFolderFor(string installDrive, string appName)
        {
            string safe = string.Concat((appName ?? "App")
                .Split(Path.GetInvalidFileNameChars()));
            if (string.IsNullOrWhiteSpace(safe)) safe = "App";
            return Path.Combine(ProgramFilesOn(installDrive), safe);
        }

        /// <summary>
        /// Best-effort аргумент пути установки для прямых установщиков.
        /// Применяется только при выборе несистемного диска, чтобы не ломать
        /// штатные тихие установки на системный диск.
        /// </summary>
        private static string BuildInstallerLocationArg(bool isMsi, string? installDrive, string appName)
        {
            if (!IsNonSystemDrive(installDrive)) return "";
            if (!isMsi) return ""; // формат EXE-установщика неизвестен — не вмешиваемся
            string target = TargetFolderFor(installDrive!, appName);
            return $" INSTALLDIR=\"{target}\"";
        }

        private async Task<bool> RunWingetAsync(string appId, string source, CancellationToken token, string? version = null, string? installDrive = null)
        {
            var profile = ProfileService.Current;
            string silent = profile.SilentInstall ? " --silent" : "";

            // Применяем выбранный диск установки. Для несистемного диска —
            // «{диск}:\Program Files»; иначе используем DefaultInstallFolder из профиля.
            string location;
            if (IsNonSystemDrive(installDrive))
            {
                string driveUpper = installDrive!.TrimEnd('\\', '/').ToUpperInvariant();
                bool folderOnSameDrive = !string.IsNullOrWhiteSpace(profile.DefaultInstallFolder) &&
                    profile.DefaultInstallFolder.TrimEnd('\\', '/').ToUpperInvariant().StartsWith(driveUpper);
                location = folderOnSameDrive
                    ? $" --location \"{profile.DefaultInstallFolder}\""
                    : $" --location \"{ProgramFilesOn(installDrive!)}\"";
            }
            else if (!string.IsNullOrWhiteSpace(profile.DefaultInstallFolder) && CommandLineGuard.ValidateInstallFolder(profile.DefaultInstallFolder))
                location = $" --location \"{profile.DefaultInstallFolder}\"";
            else
                location = "";

            // msstore/MSIX игнорируют --location или завершаются с ошибкой
            if (source.Equals("msstore", StringComparison.OrdinalIgnoreCase))
                location = "";

            string versionArg = !string.IsNullOrEmpty(version) ? $" --version \"{version}\"" : "";
            string args = $"install --id \"{appId}\" -e --source \"{source}\"{versionArg} --accept-package-agreements --accept-source-agreements --disable-interactivity{silent}{location}";

            var psi = new ProcessStartInfo
            {
                FileName = "winget.exe",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

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
                    return false;
                }
                catch (FileNotFoundException)
                {
                    Log($"❌ winget не найден — источник Winget пропущен ({appId})");
                    return false;
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
                if (process.ExitCode == 3010)
                    Log($"⚠ Установлено. Требуется перезагрузка. ({appId})");
                return process.ExitCode == 0 || process.ExitCode == 3010;
            }
        }

        private void Log(string message)
        {
            try
            {
                lock (_logLock)
                {
                    File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss} - {message}\n");
                }
            }
            catch { }
        }

        public string GetLogPath() => _logPath;

        public void Dispose()
        {
            // HttpClient общий (static) — живёт всё время работы приложения, не освобождается здесь.
        }
    }

    public class AppInstallProgress
    {
        public string AppId { get; set; } = string.Empty;
        public string AppName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int Percentage { get; set; }
        public bool IsIndeterminate { get; set; }
    }
}