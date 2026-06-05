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
        private readonly HttpClient _httpClient;
        private readonly string _logPath;
        private readonly object _logLock = new object();

        public InstallationService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools");
            _httpClient.Timeout = TimeSpan.FromSeconds(30);

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
                    var psiLocal = new ProcessStartInfo
                    {
                        FileName        = isMsi ? "msiexec" : app.LocalInstallerPath,
                        Arguments       = isMsi
                                          ? $"/i \"{app.LocalInstallerPath}\" /quiet /norestart"
                                          : (string.IsNullOrWhiteSpace(app.SilentArgs) ? "/S" : app.SilentArgs),
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
                        if (token.IsCancellationRequested) { try { localProc.Kill(); } catch { } token.ThrowIfCancellationRequested(); }
                        await Task.Delay(100, token);
                    }

                    if (localProc.ExitCode == 0)
                    {
                        appProgress.Status = "✅ Установлено (локальный файл)";
                        appProgress.Percentage = 100;
                        progress.Report(appProgress);
                        Log($"✅ {app.DisplayName} — локальный установщик");
                        await InstallHistoryService.Instance.TrackAsync(app.Id, app.DisplayName, "local", app.CategoryString);
                        return (true, "Установлено из локального файла", appProgress);
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

                    var psiCache = new ProcessStartInfo
                    {
                        FileName       = cachedPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? "msiexec" : cachedPath,
                        Arguments      = cachedPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                                         ? $"/i \"{cachedPath}\" /quiet /norestart"
                                         : "/S /silent /quiet",
                        UseShellExecute = true, Verb = "runas",
                        WindowStyle     = ProcessWindowStyle.Hidden
                    };
                    using var cacheProc = Process.Start(psiCache);
                    if (cacheProc != null)
                    {
                        while (!cacheProc.HasExited)
                        {
                            if (token.IsCancellationRequested) { try { cacheProc.Kill(); } catch { } token.ThrowIfCancellationRequested(); }
                            await Task.Delay(100, token);
                        }
                        if (cacheProc.ExitCode == 0)
                        {
                            appProgress.Status = "✅ Установлено (кэш)";
                            appProgress.Percentage = 100;
                            progress.Report(appProgress);
                            Log($"✅ {app.DisplayName} установлено из офлайн-кэша");
                            return (true, "Установлено из офлайн-кэша", appProgress);
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

                                if (await RunWingetAsync(primaryId, wsrc, token, version))
                                {
                                    appProgress.Status = "✅ Установлено (Winget)";
                                    appProgress.Percentage = 100;
                                    progress.Report(appProgress);
                                    Log($"✅ {app.DisplayName} — Winget ({wsrc}): {primaryId}");
                                    await StatsService.Instance.TrackOverrideAsync(app.Id, primaryId, null, true);
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
                                await StatsService.Instance.TrackOverrideAsync(app.Id, app.ChocoId, null, true);
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
                                await StatsService.Instance.TrackOverrideAsync(app.Id, app.ScoopId, null, true);
                                await InstallHistoryService.Instance.TrackAsync(app.Id, app.DisplayName, "scoop", app.CategoryString);
                                return (true, "Установлено через Scoop", appProgress);
                            }
                            break;
                        }

                        // ── Direct download ─────────────────────────────────────
                        case SourceOrderSettings.Direct:
                        {
                            if (!app.InstallerUrls.Any()) break;
                            foreach (var url in app.InstallerUrls)
                            {
                                token.ThrowIfCancellationRequested();
                                appProgress.Status = "📥 Скачивание...";
                                appProgress.Percentage = 20;
                                progress.Report(appProgress);

                                string tempFile = Path.Combine(Path.GetTempPath(), $"{app.Id}_{Guid.NewGuid()}.exe");
                                try
                                {
                                    using (var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
                                    {
                                        response.EnsureSuccessStatusCode();
                                        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                                        using var contentStream = await response.Content.ReadAsStreamAsync();
                                        using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);
                                        var buf = new byte[8192];
                                        long totalRead = 0;
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

                                    appProgress.Status = "🔐 Проверка SHA256...";
                                    appProgress.Percentage = 55;
                                    progress.Report(appProgress);

                                    if (!await HashHelper.VerifyHashAsync(tempFile, app.Sha256 ?? ""))
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

                                    string silentArgs = app.SilentArgs;
                                    if (string.IsNullOrWhiteSpace(silentArgs) && ProfileService.Current.SilentInstall)
                                        silentArgs = "/S";

                                    var psi = new ProcessStartInfo
                                    {
                                        FileName = tempFile, Arguments = silentArgs,
                                        UseShellExecute = true, Verb = "runas",
                                        WindowStyle = ProcessWindowStyle.Hidden
                                    };
                                    using var proc = Process.Start(psi);
                                    if (proc != null)
                                    {
                                        while (!proc.HasExited)
                                        {
                                            if (token.IsCancellationRequested) { try { proc.Kill(); } catch { } token.ThrowIfCancellationRequested(); }
                                            await Task.Delay(100, token);
                                        }
                                        try { File.Delete(tempFile); } catch { }
                                        if (proc.ExitCode == 0)
                                        {
                                            appProgress.Status = "✅ Установлено (прямая ссылка)";
                                            appProgress.Percentage = 100;
                                            progress.Report(appProgress);
                                            Log($"✅ {app.DisplayName} — прямая ссылка: {url}");
                                            await StatsService.Instance.TrackOverrideAsync(app.Id, null, url, true);
                                            await InstallHistoryService.Instance.TrackAsync(app.Id, app.DisplayName, "direct", app.CategoryString);
                                            return (true, "Установлено", appProgress);
                                        }
                                    }
                                }
                                catch (OperationCanceledException) { throw; }
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

        private async Task<bool> RunWingetAsync(string appId, string source, CancellationToken token, string? version = null)
        {
            var profile = ProfileService.Current;
            string silent = profile.SilentInstall ? " --silent" : "";
            string location = !string.IsNullOrWhiteSpace(profile.DefaultInstallFolder)
                ? $" --location \"{profile.DefaultInstallFolder}\""
                : "";
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

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                while (!process.HasExited)
                {
                    if (token.IsCancellationRequested)
                    {
                        try { process.Kill(); } catch { }
                        token.ThrowIfCancellationRequested();
                    }
                    await Task.Delay(100, token);
                }

                return process.ExitCode == 0;
            }
        }

        public bool CheckDiskSpace(long requiredMB, out long availableMB, string? installDrive = null)
        {
            try
            {
                string drivePath = !string.IsNullOrEmpty(installDrive)
                    ? installDrive
                    : Path.GetPathRoot(Environment.SystemDirectory)!;
                var drive = new DriveInfo(drivePath);
                availableMB = drive.AvailableFreeSpace / 1024 / 1024;
                return availableMB >= requiredMB;
            }
            catch
            {
                availableMB = 0;
                return true;
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
            _httpClient?.Dispose();
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