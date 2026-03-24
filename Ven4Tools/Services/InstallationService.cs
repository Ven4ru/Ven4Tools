using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Models;

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
            IProgress<AppInstallProgress> progress, string installDrive)
        {
            var appProgress = new AppInstallProgress { AppId = app.Id, AppName = app.DisplayName };

            try
            {
                Log($"Начало установки: {app.DisplayName}");
                appProgress.Status = "Начинаем...";
                progress.Report(appProgress);

                token.ThrowIfCancellationRequested();

                string primaryId = !string.IsNullOrEmpty(app.AlternativeId) ? app.AlternativeId : app.Id;

                if (!string.IsNullOrEmpty(primaryId) && !primaryId.StartsWith("User."))
                {
                    foreach (var source in wingetSources)
                    {
                        token.ThrowIfCancellationRequested();

                        appProgress.Status = $"Winget ({source})...";
                        appProgress.Percentage = 10;
                        progress.Report(appProgress);

                        bool success = await RunWingetAsync(primaryId, source, token);
                        if (success)
                        {
                            appProgress.Status = "✅ Установлено (Winget)";
                            appProgress.Percentage = 100;
                            progress.Report(appProgress);
                            Log($"✅ {app.DisplayName} установлено через Winget ({source}) с ID: {primaryId}");

                            await StatsService.Instance.TrackOverrideAsync(app.Id, primaryId, null, true);

                            return (true, "Установлено через Winget", appProgress);
                        }
                    }
                }

                token.ThrowIfCancellationRequested();

                if (app.InstallerUrls.Any())
                {
                    foreach (var url in app.InstallerUrls)
                    {
                        token.ThrowIfCancellationRequested();

                        appProgress.Status = $"📥 Скачивание...";
                        appProgress.Percentage = 20;
                        progress.Report(appProgress);

                        string tempFile = Path.Combine(Path.GetTempPath(), $"{app.Id}_{Guid.NewGuid()}.exe");

                        try
                        {
                            using (var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
                            {
                                response.EnsureSuccessStatusCode();
                                var totalBytes = response.Content.Headers.ContentLength ?? -1L;

                                using (var contentStream = await response.Content.ReadAsStreamAsync())
                                using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                                {
                                    var buffer = new byte[8192];
                                    var totalBytesRead = 0L;
                                    int bytesRead;

                                    while ((bytesRead = await contentStream.ReadAsync(buffer, token)) > 0)
                                    {
                                        token.ThrowIfCancellationRequested();
                                        await fileStream.WriteAsync(buffer, 0, bytesRead, token);
                                        totalBytesRead += bytesRead;

                                        if (totalBytes != -1)
                                        {
                                            appProgress.Percentage = 20 + (int)((double)totalBytesRead / totalBytes * 30);
                                            progress.Report(appProgress);
                                        }
                                    }
                                }
                            }

                            token.ThrowIfCancellationRequested();

                            appProgress.Status = $"⚙️ Установка...";
                            appProgress.Percentage = 60;
                            progress.Report(appProgress);

                            var psi = new ProcessStartInfo
                            {
                                FileName = tempFile,
                                Arguments = app.SilentArgs,
                                UseShellExecute = true,
                                Verb = "runas",
                                WindowStyle = ProcessWindowStyle.Hidden
                            };

                            var process = Process.Start(psi);
                            if (process != null)
                            {
                                while (!process.HasExited)
                                {
                                    if (token.IsCancellationRequested)
                                    {
                                        try { process.Kill(); } catch { }
                                        token.ThrowIfCancellationRequested();
                                    }
                                    await Task.Delay(100, token);
                                }

                                try { File.Delete(tempFile); } catch { }

                                if (process.ExitCode == 0)
                                {
                                    appProgress.Status = "✅ Установлено";
                                    appProgress.Percentage = 100;
                                    progress.Report(appProgress);
                                    Log($"✅ {app.DisplayName} установлено через прямую ссылку: {url}");

                                    await StatsService.Instance.TrackOverrideAsync(app.Id, null, url, true);

                                    return (true, "Установлено", appProgress);
                                }
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Log($"❌ Ошибка при установке {app.DisplayName} по ссылке {url}: {ex.Message}");
                            try { File.Delete(tempFile); } catch { }
                            continue;
                        }
                    }
                }

                appProgress.Status = "❌ Ошибка";
                progress.Report(appProgress);
                Log($"❌ {app.DisplayName} не удалось установить");
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

        private async Task<bool> RunWingetAsync(string appId, string source, CancellationToken token)
        {
            string args = $"/c winget install --id {appId} -e --source {source} --accept-package-agreements --accept-source-agreements --disable-interactivity";

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
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

        public bool CheckDiskSpace(long requiredMB, out long availableMB)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
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