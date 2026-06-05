using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services
{
    public class UpdateBackgroundService : IDisposable
    {
        private Timer? _timer;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly string _launcherVersion;
        private readonly Func<string> _getClientPath;
        private readonly GitHubService _github = new GitHubService();
        private string _lastNotificationId = "";

        public string LastNotifiedLauncherVersion { get; set; } = "";
        public string LastNotifiedClientVersion { get; set; } = "";

        // type: "launcher" or "client"
        public event Action<string, UpdateInfo>? UpdateAvailable;
        public event Action<int>? WingetUpgradeCountChanged;
        public event Action<Notification>? NotificationAvailable;

        public UpdateBackgroundService(string launcherVersion, Func<string> getClientPath)
        {
            _launcherVersion = launcherVersion;
            _getClientPath = getClientPath;
        }

        public void Start()
        {
            // Сбрасываем токен отмены (мог быть отменён через Stop)
            if (_cts.IsCancellationRequested)
                _cts = new CancellationTokenSource();

            if (_timer == null)
                _timer = new Timer(_ => { _ = CheckAllAsync(); }, null,
                    TimeSpan.FromSeconds(60), TimeSpan.FromHours(3));
            else
                _timer.Change(TimeSpan.FromSeconds(60), TimeSpan.FromHours(3));
        }

        public void Stop()
        {
            _cts.Cancel();
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        public async Task CheckNowAsync() => await CheckAllAsync();

        private async Task CheckAllAsync()
        {
            if (_cts.IsCancellationRequested) return;
            try { await CheckLauncherAsync(); } catch { }
            if (_cts.IsCancellationRequested) return;
            try { await CheckClientAsync(); } catch { }
            if (_cts.IsCancellationRequested) return;
            try
            {
                int count = await CountWingetUpgradesAsync();
                WingetUpgradeCountChanged?.Invoke(count);
            }
            catch { }
            if (_cts.IsCancellationRequested) return;
            try
            {
                var notif = await NotificationService.GetLatestAsync();
                if (notif != null && notif.Id != _lastNotificationId && !string.IsNullOrEmpty(notif.Message))
                {
                    _lastNotificationId = notif.Id;
                    NotificationAvailable?.Invoke(notif);
                }
            }
            catch { }
        }

        private async Task CheckLauncherAsync()
        {
            var info = await _github.CheckLauncherUpdate(_launcherVersion);
            if (info?.HasUpdate != true) return;
            if (info.LatestVersion == LastNotifiedLauncherVersion) return;
            if (_cts.IsCancellationRequested) return;

            LastNotifiedLauncherVersion = info.LatestVersion!;
            UpdateAvailable?.Invoke("launcher", info);
        }

        private async Task CheckClientAsync()
        {
            string clientExe = Path.Combine(_getClientPath(), "Ven4Tools.exe");
            if (!File.Exists(clientExe)) return;

            var versionInfo = FileVersionInfo.GetVersionInfo(clientExe);
            string installedVersion = versionInfo.FileVersion ?? "0.0.0";

            var versions = await _github.GetAvailableClientVersions();
            var latest = versions.FirstOrDefault(v => v.IsLatest);
            if (latest == null) return;

            if (!VersionComparer.IsNewer(latest.Version, installedVersion)) return;
            if (latest.Version == LastNotifiedClientVersion) return;
            if (_cts.IsCancellationRequested) return;

            LastNotifiedClientVersion = latest.Version;
            UpdateAvailable?.Invoke("client", new UpdateInfo
            {
                HasUpdate = true,
                CurrentVersion = installedVersion,
                LatestVersion = latest.Version,
                DownloadUrl = latest.DownloadUrl,
                ReleaseNotes = latest.ReleaseNotes
            });
        }

        private async Task<int> CountWingetUpgradesAsync()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("winget",
                    "upgrade --include-unknown --accept-source-agreements --disable-interactivity")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return 0;
                var errTask = p.StandardError.ReadToEndAsync();
                string output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
                await errTask;
                // Count lines after the header separator "---"
                var lines = output.Split('\n');
                int headerIdx = Array.FindIndex(lines, l => l.TrimStart().StartsWith("---"));
                if (headerIdx < 0) return 0;
                return lines.Skip(headerIdx + 1).Count(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("---"));
            }
            catch { return 0; }
        }


        public void Dispose()
        {
            _cts.Cancel();
            _timer?.Dispose();
            _github.Dispose();
        }
    }
}
