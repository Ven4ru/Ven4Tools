using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services
{
    public class UpdateService
    {
        private const string GitHubApiUrl = "https://api.github.com/repos/Ven4ru/Ven4Tools/releases/latest";

        public async Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools-Launcher");

                var response = await client.GetAsync(GitHubApiUrl);
                if (!response.IsSuccessStatusCode) return null;

                var json = await response.Content.ReadAsStringAsync();
                dynamic? release = JsonConvert.DeserializeObject(json);
                if (release == null) return null;

                string remoteVersion = release.tag_name?.ToString()?.TrimStart('v') ?? "";
                string releaseNotes = release.body?.ToString() ?? "";

                // Ищем .exe asset — не берём первый попавшийся (может быть source code zip)
                string downloadUrl = "";
                if (release?.assets != null)
                {
                    foreach (var asset in release.assets)
                    {
                        string assetName = asset.name?.ToString() ?? "";
                        if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.browser_download_url?.ToString() ?? "";
                            break;
                        }
                    }
                    if (string.IsNullOrEmpty(downloadUrl))
                        downloadUrl = release.assets[0]?.browser_download_url?.ToString() ?? "";
                }

                var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                var currentVersion = ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "0.0.0";
                bool hasUpdate = VersionComparer.IsNewer(remoteVersion, currentVersion);

                return new UpdateInfo
                {
                    HasUpdate = hasUpdate,
                    LatestVersion = remoteVersion,
                    DownloadUrl = downloadUrl,
                    ReleaseNotes = releaseNotes,
                    FileSize = 0
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка проверки обновлений: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> DownloadAndInstallUpdateAsync()
        {
            try
            {
                var updateInfo = await CheckForUpdatesAsync();
                if (updateInfo == null || !updateInfo.HasUpdate) return false;
                if (string.IsNullOrEmpty(updateInfo.DownloadUrl)) return false;

                string tempFile = Path.Combine(Path.GetTempPath(), $"Ven4Tools_Launcher_{updateInfo.LatestVersion}.exe");

                using var client = new HttpClient();
                using var response = await client.GetAsync(updateInfo.DownloadUrl);
                response.EnsureSuccessStatusCode();

                using var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write);
                await response.Content.CopyToAsync(fs);

                string scriptPath = Path.Combine(Path.GetTempPath(), "update_launcher.ps1");
                string launcherPath = Process.GetCurrentProcess().MainModule?.FileName ?? "";

                // Экранируем одинарные кавычки для PS-строк (одинарная → две одинарных)
                string psTemp = tempFile.Replace("'", "''");
                string psLauncher = launcherPath.Replace("'", "''");

                string script = $@"
Start-Sleep -Seconds 2
try {{
    Copy-Item '{psTemp}' '{psLauncher}' -Force
    Start-Process '{psLauncher}'
}} catch {{
    Write-Output $_.Exception.Message
}}
Remove-Item '{psTemp}'
";
                File.WriteAllText(scriptPath, script);

                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    UseShellExecute = true
                    // CreateNoWindow is ignored when UseShellExecute=true; -WindowStyle Hidden hides the PS window instead
                });

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка обновления: {ex.Message}");
                return false;
            }
        }

    }
}