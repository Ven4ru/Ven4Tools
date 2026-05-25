using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    public class InstalledAppsService
    {
        private string _rawOutput = string.Empty;

        public async Task RefreshAsync()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "winget.exe",
                    Arguments = "list --accept-source-agreements --disable-interactivity",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                using var process = Process.Start(psi);
                if (process == null) return;

                _rawOutput = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
            }
            catch
            {
                _rawOutput = string.Empty;
            }
        }

        public bool IsInstalled(string wingetId)
        {
            if (string.IsNullOrEmpty(wingetId) || string.IsNullOrEmpty(_rawOutput))
                return false;

            return Regex.IsMatch(_rawOutput, $@"(?<!\S){Regex.Escape(wingetId)}(?!\S)", RegexOptions.IgnoreCase);
        }

        public string GetInstalledVersion(string wingetId)
        {
            if (string.IsNullOrEmpty(wingetId) || string.IsNullOrEmpty(_rawOutput))
                return string.Empty;

            var match = Regex.Match(_rawOutput, $@"(?<!\S){Regex.Escape(wingetId)}\s+(\S+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }
    }
}
