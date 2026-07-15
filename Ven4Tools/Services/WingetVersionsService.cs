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
            if (!CommandLineGuard.ValidateId(wingetId)) return new List<string>();
            try
            {
                var (_, output) = await WingetRunner.RunAsync(new[]
                {
                    "show", "--id", wingetId, "--versions", "-e", "--source", "winget"
                }, token: token);

                return ParseVersions(output);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[WingetVersionsService] Получение списка версий «{wingetId}»: {ex.Message}");
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
                    if (WingetRunner.IsTableSeparator(line)) pastSeparator = true;
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(trimmed))
                    versions.Add(trimmed);
            }

            return versions;
        }
    }
}
