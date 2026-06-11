using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public static class WingetService
    {
        /// <summary>
        /// Search winget for packages matching <paramref name="query"/>.
        /// Returns up to 15 deduplicated results (IDs must contain a dot).
        /// </summary>
        public static async Task<List<WingetPackage>> SearchAsync(
            string query, CancellationToken token = default)
        {
            var results = new List<WingetPackage>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "winget.exe",
                    Arguments = $"search --name \"{query}\" --source winget --accept-source-agreements",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };

                using var process = Process.Start(psi);
                if (process == null) return results;

                using var reg = token.Register(() => { try { process.Kill(); } catch { } });

                var stderrTask = process.StandardError.ReadToEndAsync();
                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync(token);
                await stderrTask;

                bool headerPassed = false;
                foreach (var line in output.Split(
                    new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!headerPassed)
                    {
                        if (line.Contains("--")) headerPassed = true;
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = Regex.Split(line.Trim(), @"\s{2,}");
                    if (parts.Length < 2) continue;

                    string id = parts[1].Trim();
                    if (!id.Contains('.')) continue; // настоящие winget ID всегда содержат точку

                    results.Add(new WingetPackage
                    {
                        Name    = parts[0].Trim(),
                        Id      = id,
                        Version = parts.Length > 2 ? parts[2].Trim() : "",
                        Source  = parts.Length > 3 ? parts[3].Trim() : "winget"
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch { }

            return results
                .GroupBy(p => p.Id)
                .Select(g => g.First())
                .Take(15)
                .ToList();
        }

        /// <summary>
        /// Validate a winget package by exact ID. Returns (Name, Version) or (null, null).
        /// </summary>
        public static async Task<(string? Name, string? Version)> ValidateIdAsync(string id)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "winget.exe",
                    Arguments = $"show --id {id} -e --source winget --accept-source-agreements",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };

                using var process = Process.Start(psi);
                if (process == null) return (null, null);

                var stderrTask = process.StandardError.ReadToEndAsync();
                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                await stderrTask;
                if (process.ExitCode != 0) return (null, null);

                string? name = null, version = null;
                foreach (var line in output.Split('\n'))
                {
                    var t = line.Trim();
                    if (name == null)
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(t,
                            @"(?:Found|Найдено)\s+(.+?)\s+\[");
                        if (m.Success) { name = m.Groups[1].Value.Trim(); continue; }
                    }
                    if (version == null &&
                        (t.StartsWith("Version:") || t.StartsWith("Версия:")))
                    {
                        version = t.Split(':', 2).Last().Trim();
                    }
                }
                return (name, version);
            }
            catch { return (null, null); }
        }
    }
}
