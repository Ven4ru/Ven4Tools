using System;
using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Ven4Tools.Services
{
    // Деинсталляция: winget uninstall по ID → сканирование реестра UninstallString
    // по DisplayName → тихий запуск (msiexec /x /quiet или NSIS/Inno /S /SILENT).
    // Перенесено из InstalledTab.xaml.cs (2026-07-17), чтобы карточка приложения
    // в каталоге могла переиспользовать ту же логику вместо копирования.
    public static class AppUninstallService
    {
        public static async Task<bool> TryUninstallAsync(string? wingetId, string displayName)
        {
            // Попытка 1: winget uninstall по ID (работает для пакетов с непустым Source)
            if (!string.IsNullOrWhiteSpace(wingetId) && !wingetId.Contains('…'))
            {
                string args = $"uninstall --id \"{wingetId}\" --silent --accept-source-agreements";
                var (exitCode, _) = await WingetRunner.RunAsync(args);
                // 0 = успех, 0x8A150014 = пакет не установлен (нечего удалять — считаем успехом).
                if (exitCode == 0 || exitCode == unchecked((int)0x8A150014))
                    return true;
            }

            // Попытка 2: найти строку UninstallString в реестре по DisplayName.
            string? uninstallString = await Task.Run(() => FindUninstallString(displayName));
            if (uninstallString != null)
                return await RunUninstallStringAsync(uninstallString);

            return false;
        }

        private static string? FindUninstallString(string displayName)
        {
            string[] keys = {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            // HKCU покрывает user-scope установки (Chrome/Discord/VS Code и т.п.
            // при выборе "только для меня"), которых нет в HKLM — до этого сканировался
            // только HKLM, откат деинсталляции для таких приложений молча проваливался.
            var hives = new[] { Registry.LocalMachine, Registry.CurrentUser };

            foreach (var hive in hives)
            foreach (var keyPath in keys)
            {
                using var root = hive.OpenSubKey(keyPath);
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var entry = root.OpenSubKey(sub);
                    if (entry == null) continue;
                    var name = entry.GetValue("DisplayName")?.ToString();
                    if (name != null && name.Equals(displayName, StringComparison.OrdinalIgnoreCase))
                        return entry.GetValue("UninstallString")?.ToString();
                }
            }
            return null;
        }

        private static async Task<bool> RunUninstallStringAsync(string uninstallString)
        {
            string cmd = uninstallString.Trim();
            Process? p;
            if (cmd.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase) ||
                cmd.StartsWith("msiexec", StringComparison.OrdinalIgnoreCase))
            {
                var productCode = Regex.Match(cmd, @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}");
                if (!productCode.Success)
                    return false;

                var startInfo = new ProcessStartInfo(TrustedExecutablePaths.MsiExec)
                {
                    UseShellExecute = true,
                    Verb            = "runas",
                    CreateNoWindow  = true
                };
                startInfo.ArgumentList.Add("/x");
                startInfo.ArgumentList.Add(productCode.Value);
                startInfo.ArgumentList.Add("/quiet");
                startInfo.ArgumentList.Add("/norestart");
                p = Process.Start(startInfo);
            }
            else
            {
                string exe = cmd, args = "";
                if (cmd.StartsWith("\""))
                {
                    int end = cmd.IndexOf('"', 1);
                    if (end > 0) { exe = cmd.Substring(1, end - 1); args = cmd.Substring(end + 1).Trim(); }
                }
                else
                {
                    int searchFrom = 0;
                    int bestSplit = -1;
                    while (true)
                    {
                        int sp = cmd.IndexOf(' ', searchFrom);
                        if (sp < 0) break;
                        string candidate = cmd.Substring(0, sp);
                        if (File.Exists(candidate)) bestSplit = sp;
                        searchFrom = sp + 1;
                    }

                    if (bestSplit > 0)
                    {
                        exe = cmd.Substring(0, bestSplit);
                        args = cmd.Substring(bestSplit + 1).Trim();
                    }
                    else if (!File.Exists(cmd))
                    {
                        int sp = cmd.IndexOf(' ');
                        if (sp > 0) { exe = cmd.Substring(0, sp); args = cmd.Substring(sp + 1).Trim(); }
                    }
                }
                if (!args.Contains("/S") && !args.Contains("/SILENT") && !args.Contains("/silent"))
                    args = "/S " + args;

                if (!File.Exists(exe)) return false;

                p = Process.Start(new ProcessStartInfo(exe, args)
                    { UseShellExecute = true, Verb = "runas" });
            }
            if (p == null) return false;
            using (p)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                    await p.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(); } catch { }
                    return false;
                }
                // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED — удаление прошло успешно
                return p.ExitCode == 0 || p.ExitCode == 3010;
            }
        }
    }
}
