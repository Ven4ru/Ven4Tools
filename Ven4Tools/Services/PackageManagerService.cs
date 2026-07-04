using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    public static class PackageManagerService
    {
        // ── Chocolatey ────────────────────────────────────────────────────────────

        public static bool IsChocoInstalled()
        {
            try
            {
                var psi = new ProcessStartInfo("choco.exe", "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                // Читаем потоки в фоне — иначе дедлок если буфер переполнится
                var outTask = Task.Run(() => p.StandardOutput.ReadToEnd());
                var errTask = Task.Run(() => p.StandardError.ReadToEnd());
                bool exited = p.WaitForExit(5000);
                if (!exited) { try { p.Kill(); } catch { } }
                return exited && p.ExitCode == 0;
            }
            catch { return false; }
        }

        public static async Task<bool> InstallChocoAsync(Action<string>? log = null)
        {
            log?.Invoke("📦 Установка Chocolatey...");
            try
            {
                // Official Chocolatey install script
                string cmd =
                    "Set-ExecutionPolicy Bypass -Scope Process -Force; " +
                    "[System.Net.ServicePointManager]::SecurityProtocol = " +
                    "[System.Net.ServicePointManager]::SecurityProtocol -bor 3072; " +
                    "iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{cmd}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                await Task.WhenAll(
                    p.StandardOutput.ReadToEndAsync(),
                    p.StandardError.ReadToEndAsync());
                await p.WaitForExitAsync();
                bool ok = IsChocoInstalled();
                log?.Invoke(ok ? "✅ Chocolatey установлен" : "⚠️ Chocolatey не найден после установки");
                return ok;
            }
            catch (Exception ex)
            {
                log?.Invoke($"❌ Ошибка установки Chocolatey: {ex.Message}");
                return false;
            }
        }

        public static async Task<bool> RunChocoInstallAsync(
            string packageId, CancellationToken token, Action<string>? log = null)
        {
            if (!CommandLineGuard.ValidateId(packageId))
            {
                log?.Invoke($"❌ Choco: недопустимый идентификатор пакета «{packageId}»");
                return false;
            }
            log?.Invoke($"🍫 Choco: установка {packageId}...");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "choco.exe",
                    Arguments = $"install \"{packageId}\" -y --no-progress --limit-output",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };
                using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) log?.Invoke($"  choco: {e.Data}"); };
                p.ErrorDataReceived  += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) log?.Invoke($"  choco: {e.Data}"); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                while (!p.HasExited)
                {
                    if (token.IsCancellationRequested)
                    {
                        try { p.Kill(entireProcessTree: true); } catch { }
                        token.ThrowIfCancellationRequested();
                    }
                    await Task.Delay(100, token);
                }
                return p.ExitCode == 0;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log?.Invoke($"❌ Choco: {ex.Message}"); return false; }
        }

        // ── Scoop ─────────────────────────────────────────────────────────────────

        public static bool IsScoopInstalled()
        {
            string shimPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "scoop", "shims", "scoop.cmd");
            if (File.Exists(shimPath)) return true;

            try
            {
                var psi = new ProcessStartInfo("scoop", "help")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                var outTask = Task.Run(() => p.StandardOutput.ReadToEnd());
                var errTask = Task.Run(() => p.StandardError.ReadToEnd());
                bool exited = p.WaitForExit(5000);
                if (!exited) { try { p.Kill(); } catch { } }
                return exited && p.ExitCode == 0;
            }
            catch { return false; }
        }

        public static async Task<bool> InstallScoopAsync(Action<string>? log = null)
        {
            log?.Invoke("📦 Установка Scoop...");
            try
            {
                string cmd =
                    "Set-ExecutionPolicy RemoteSigned -Scope CurrentUser -Force; " +
                    "Invoke-RestMethod -Uri https://get.scoop.sh | Invoke-Expression";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{cmd}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                await Task.WhenAll(
                    p.StandardOutput.ReadToEndAsync(),
                    p.StandardError.ReadToEndAsync());
                await p.WaitForExitAsync();
                bool ok = IsScoopInstalled();
                log?.Invoke(ok ? "✅ Scoop установлен" : "⚠️ Scoop не найден после установки");
                return ok;
            }
            catch (Exception ex)
            {
                log?.Invoke($"❌ Ошибка установки Scoop: {ex.Message}");
                return false;
            }
        }

        // Большинство манифестов каталога (element, putty, autohotkey, aida64extreme, ddu и др.)
        // лежат не в main, а в extras; Steam — в versions. Ни один из них не добавляется
        // автоматически при установке Scoop, поэтому install падает с "couldn't find manifest".
        private static readonly string[] RequiredScoopBuckets = { "extras", "versions" };

        private static async Task EnsureScoopBucketsAsync(Action<string>? log)
        {
            string scoopExe = GetScoopExe();
            string existing;
            try
            {
                var listPsi = new ProcessStartInfo(scoopExe, "bucket list")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };
                using var listProc = Process.Start(listPsi);
                if (listProc == null) return;
                existing = await listProc.StandardOutput.ReadToEndAsync();
                await listProc.WaitForExitAsync();
            }
            catch { return; }

            foreach (var bucket in RequiredScoopBuckets)
            {
                if (existing.Contains(bucket, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var addPsi = new ProcessStartInfo(scoopExe, $"bucket add {bucket}")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var addProc = Process.Start(addPsi);
                    if (addProc == null) continue;
                    await addProc.WaitForExitAsync();
                    log?.Invoke(addProc.ExitCode == 0
                        ? $"🪣 Scoop: добавлен бакет {bucket}"
                        : $"⚠️ Scoop: не удалось добавить бакет {bucket}");
                }
                catch (Exception ex) { log?.Invoke($"⚠️ Scoop bucket add {bucket}: {ex.Message}"); }
            }
        }

        public static async Task<bool> RunScoopInstallAsync(
            string packageId, CancellationToken token, Action<string>? log = null)
        {
            if (!CommandLineGuard.ValidateId(packageId))
            {
                log?.Invoke($"❌ Scoop: недопустимый идентификатор пакета «{packageId}»");
                return false;
            }
            log?.Invoke($"🪣 Scoop: установка {packageId}...");
            string scoopExe = GetScoopExe();
            await EnsureScoopBucketsAsync(log);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = scoopExe,
                    Arguments = $"install \"{packageId}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };
                using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) log?.Invoke($"  scoop: {e.Data}"); };
                p.ErrorDataReceived  += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) log?.Invoke($"  scoop: {e.Data}"); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                while (!p.HasExited)
                {
                    if (token.IsCancellationRequested)
                    {
                        try { p.Kill(entireProcessTree: true); } catch { }
                        token.ThrowIfCancellationRequested();
                    }
                    await Task.Delay(100, token);
                }
                return p.ExitCode == 0;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log?.Invoke($"❌ Scoop: {ex.Message}"); return false; }
        }

        // ── Search ────────────────────────────────────────────────────────────────

        public static async Task<List<(string Id, string Name, string Version)>> SearchChocoAsync(
            string query, CancellationToken token = default)
        {
            var results = new List<(string, string, string)>();
            query = CommandLineGuard.SanitizeQuery(query);
            if (string.IsNullOrEmpty(query) || !IsChocoInstalled()) return results;
            try
            {
                var psi = new ProcessStartInfo("choco.exe",
                    $"search \"{query}\" --limit-output --page-size 8")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute = false, CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding  = System.Text.Encoding.UTF8
                };
                using var p = Process.Start(psi);
                if (p == null) return results;
                var errTask = p.StandardError.ReadToEndAsync();
                string output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync(token);
                await errTask;
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Trim().Split('|');
                    if (parts.Length >= 2)
                        results.Add((parts[0].Trim(), parts[0].Trim(), parts[1].Trim()));
                }
            }
            catch (Exception ex) { AppLogger.Write($"[PackageManagerService] Поиск в Chocolatey: {ex.Message}"); }
            return results;
        }

        public static async Task<List<(string Id, string Name)>> SearchScoopAsync(
            string query, CancellationToken token = default)
        {
            var results = new List<(string, string)>();
            query = CommandLineGuard.SanitizeQuery(query);
            if (string.IsNullOrEmpty(query) || !IsScoopInstalled()) return results;
            try
            {
                string scoopExe = GetScoopExe();
                var psi = new ProcessStartInfo(scoopExe, $"search \"{query}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute = false, CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding  = System.Text.Encoding.UTF8
                };
                using var p = Process.Start(psi);
                if (p == null) return results;
                var errTask = p.StandardError.ReadToEndAsync();
                string output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync(token);
                await errTask;

                bool inResults = false;
                foreach (var rawLine in output.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith("---")) { inResults = true; continue; }
                    if (!inResults || string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1)
                        results.Add((parts[0], parts[0]));
                    if (results.Count >= 8) break;
                }
            }
            catch (Exception ex) { AppLogger.Write($"[PackageManagerService] Поиск в Scoop: {ex.Message}"); }
            return results;
        }

        private static string GetScoopExe()
        {
            string shimCmd = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "scoop", "shims", "scoop.cmd");
            return File.Exists(shimCmd) ? shimCmd : "scoop";
        }
    }
}
