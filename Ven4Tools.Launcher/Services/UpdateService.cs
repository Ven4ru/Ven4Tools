using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Ven4Tools.Launcher.Models;  // ← ДОБАВИТЬ ЭТУ СТРОКУ

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
                string downloadUrl = release.assets?[0]?.browser_download_url?.ToString() ?? "";
                string releaseNotes = release.body?.ToString() ?? "";

                var currentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "2.3.0";
                bool hasUpdate = CompareVersions(remoteVersion, currentVersion) > 0;

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

                string tempFile = Path.Combine(Path.GetTempPath(), $"Ven4Tools_Launcher_{updateInfo.LatestVersion}.exe");

                using var client = new HttpClient();
                using var response = await client.GetAsync(updateInfo.DownloadUrl);
                response.EnsureSuccessStatusCode();

                using var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write);
                await response.Content.CopyToAsync(fs);

                string scriptPath = Path.Combine(Path.GetTempPath(), "update_launcher.ps1");
                string launcherPath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                string script = @"
Start-Sleep -Seconds 2
try {
    Copy-Item '" + tempFile + @"' '" + launcherPath + @"' -Force
    Start-Process '" + launcherPath + @"'
} catch {
    Write-Output $_.Exception.Message
}
Remove-Item '" + tempFile + @"'
";
                File.WriteAllText(scriptPath, script);

                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    UseShellExecute = true,
                    CreateNoWindow = true
                });

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка обновления: {ex.Message}");
                return false;
            }
        }

        private int CompareVersions(string v1, string v2)
        {
            var parts1 = v1.Split('.');
            var parts2 = v2.Split('.');
            for (int i = 0; i < Math.Max(parts1.Length, parts2.Length); i++)
            {
                int num1 = i < parts1.Length ? int.Parse(parts1[i]) : 0;
                int num2 = i < parts2.Length ? int.Parse(parts2[i]) : 0;
                if (num1 != num2) return num1.CompareTo(num2);
            }
            return 0;
        }
    }
}