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
        // volatile: поле переприсваивается из UI-потока (Start), а читается
        // из ThreadPool-потока таймера — гарантируем видимость актуальной ссылки
        private volatile CancellationTokenSource _cts = new CancellationTokenSource();
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

        // Убрать ANSI escape-коды из вывода winget перед парсингом.
        // [0-9;?]* — параметры CSI включая private-mode prefix '?'; lh — cursor hide/show.
        private static readonly System.Text.RegularExpressions.Regex _ansiRegex =
            new(@"\x1B(?:\[[0-9;?]*[mGKHFABCDsuJhlLM]|\][^\x07]*\x07|[()][0-9A-Za-z])",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string StripAnsi(string s) => _ansiRegex.Replace(s, "");

        private async Task<int> CountWingetUpgradesAsync()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("winget",
                    "upgrade --include-unknown --accept-source-agreements --disable-interactivity --locale en-US")
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
                // Считаем только строки таблицы между разделителем «---» и футером.
                // Футер winget («N upgrades available.») отделён пустой строкой —
                // на ней останавливаемся, чтобы не считать его за приложение.
                output = StripAnsi(output);
                var lines = output.Replace("\r", "").Split('\n');
                int sepIdx = Array.FindIndex(lines, l =>
                {
                    string t = l.Trim();
                    return t.Length >= 5 && t.Contains('-') && t.All(c => c == '-' || c == ' ');
                });
                if (sepIdx < 0) return 0;

                int count = 0;
                for (int i = sepIdx + 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) break; // начался футер
                    string t = line.Trim();
                    if (t.All(c => c == '-' || c == ' ')) continue; // ещё один разделитель
                    // Строки-суммарники футера winget («N upgrades available.» / «N package(s)...»)
                    if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d+\b.*(package|upgrade)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase)) break;
                    count++;
                }
                return count;
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
