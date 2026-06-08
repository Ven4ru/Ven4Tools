using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    // Универсальные методы запуска winget. Не для Launcher — он самостоятельный проект.
    internal static class WingetRunner
    {
        // [0-9;?]* — параметры CSI включая private-mode '?'; lLM — cursor hide/show.
        private static readonly Regex _ansiRegex =
            new(@"\x1B(?:\[[0-9;?]*[mGKHFABCDsuJhlLM]|\][^\x07]*\x07|[()][0-9A-Za-z])",
                RegexOptions.Compiled);

        public static string StripAnsi(string s) => _ansiRegex.Replace(s, "");

        // Запуск winget, захват stdout целиком. Таймаут по умолчанию — 120 с.
        public static async Task<string> RunAsync(string args, TimeSpan? timeout = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "winget",
                Arguments              = args,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("winget не найден");
            using var cts = new System.Threading.CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(120));
            var outputTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = Task.Run(() => p.StandardError.ReadToEnd());
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(); } catch { }
            }
            string output = await outputTask;
            await stderrTask;
            return output;
        }

        // Построчный стриминг вывода winget с фильтрацией прогресс-бара и ANSI.
        // Таймаут по умолчанию — 45 мин (upgrade --all).
        public static async Task<int> RunStreamingAsync(string args, Action<string> onLine, TimeSpan? timeout = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "winget",
                Arguments              = args,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("winget не найден");
            using var cts = new System.Threading.CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(45));
            var stderrTask = Task.Run(() => p.StandardError.ReadToEnd());

            string? raw;
            string last = "";
            try
            {
                while ((raw = await p.StandardOutput.ReadLineAsync(cts.Token)) != null)
                {
                    string clean = StripAnsi(raw).Trim();
                    if (string.IsNullOrWhiteSpace(clean)) continue;
                    // Пропускаем строки прогресс-бара (только псевдографика/проценты/размеры)
                    if (clean.All(c => c is '-' or '─' or '█' or '▒' or '░' or '\\' or '|'
                                         or '/' or '%' or ' ' or '.' or 'K' or 'M' or 'B' or 'G'
                                         || (c >= '0' && c <= '9')))
                        continue;
                    if (clean == last) continue;
                    last = clean;
                    onLine(clean);
                }
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                onLine("⚠ winget завис — принудительное завершение");
                return -1;
            }
            string stderrOutput = await stderrTask;
            if (p.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderrOutput))
                onLine($"[stderr] {stderrOutput.Trim().Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? ""}");
            return p.ExitCode;
        }
    }
}
