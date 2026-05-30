using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    public static class WingetVersionsService
    {
        public static async Task<List<string>> FetchVersionsAsync(string wingetId, CancellationToken token = default)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "winget",
                    Arguments = $"show --id {wingetId} --versions -e --source winget",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };

                using var process = Process.Start(psi);
                if (process == null) return new List<string>();

                var stderrTask = process.StandardError.ReadToEndAsync();
                string output = await process.StandardOutput.ReadToEndAsync(token);
                await process.WaitForExitAsync(token);
                await stderrTask;

                return ParseVersions(output);
            }
            catch
            {
                return new List<string>();
            }
        }

        private static List<string> ParseVersions(string output)
        {
            var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            bool pastSeparator = false;
            var versions = new List<string>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!pastSeparator)
                {
                    if (trimmed.StartsWith("---")) pastSeparator = true;
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(trimmed))
                    versions.Add(trimmed);
            }

            return versions;
        }
    }
}
