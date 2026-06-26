using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    // Универсальные методы запуска winget. Не для Launcher — он самостоятельный проект.
    internal static class WingetRunner
    {
        // Белый список символов для аргумента winget (id пакета, флаги, значения).
        // Дополнительно к набору из аудита разрешён '+' — он встречается в реальных
        // id пакетов (например "Notepad++.Notepad++"), его наличие безопасно.
        private static readonly Regex _safeArgRegex =
            new(@"^[A-Za-z0-9._\-+{}()]+$", RegexOptions.Compiled);

        // Флаги, после которых идёт путь к файлу (export/import). Для них значение
        // в кавычках — это путь, выбранный пользователем через системный диалог,
        // поэтому к нему белый список символов не применяется.
        private static readonly HashSet<string> _pathFlags =
            new(StringComparer.OrdinalIgnoreCase)
            { "-o", "--output", "-i", "--import-file", "--manifest", "-m" };

        private static bool IsArgSafe(string arg) => _safeArgRegex.IsMatch(arg);

        // Разбивает строку аргументов на токены с учётом кавычек. Возвращает текст
        // токена и признак того, был ли он заключён в двойные кавычки.
        private static List<(string Value, bool Quoted)> TokenizeArgs(string args)
        {
            var result = new List<(string, bool)>();
            var sb = new StringBuilder();
            bool inQuotes = false, quoted = false, hasToken = false;

            foreach (char c in args)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    quoted = true;
                    hasToken = true;
                    continue;
                }
                if (!inQuotes && char.IsWhiteSpace(c))
                {
                    if (hasToken)
                    {
                        result.Add((sb.ToString(), quoted));
                        sb.Clear();
                        quoted = false;
                        hasToken = false;
                    }
                    continue;
                }
                sb.Append(c);
                hasToken = true;
            }
            if (hasToken) result.Add((sb.ToString(), quoted));
            return result;
        }

        // Проверяет строку аргументов перед запуском процесса. Защита от инъекции
        // дополнительных winget-флагов через скомпрометированный id пакета
        // (например 'Foo" --override "/payload'): попытка «вырваться» из кавычек
        // создаёт токены, которые не проходят белый список → ArgumentException.
        private static void ValidateArgs(string args)
        {
            string prev = "";
            foreach (var (value, quoted) in TokenizeArgs(args))
            {
                // Значение в кавычках сразу после флага пути (-o/-i и т.п.) —
                // это путь к файлу из системного диалога, проверку символов пропускаем.
                bool isTrustedPath = quoted && _pathFlags.Contains(prev);
                if (!isTrustedPath && !IsArgSafe(value))
                    throw new ArgumentException($"Недопустимый аргумент winget: '{value}'");
                prev = value;
            }
        }
        // [0-9;?]* — параметры CSI включая private-mode '?'; lLM — cursor hide/show.
        // OSC закрывается либо BEL (\x07), либо ST (ESC \). C1 CSI (\x9B) — однобайтовый
        // вариант ESC[ в наборе C1, тоже вырезаем.
        private static readonly Regex _ansiRegex =
            new(@"\x1B(?:\[[0-9;?]*[mGKHFABCDsuJhlLM]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[()][0-9A-Za-z])|\x9B[0-9;?]*[mGKHFABCDsuJhlLM]",
                RegexOptions.Compiled);

        public static string StripAnsi(string s) => _ansiRegex.Replace(s, "");

        // Некоторые версии winget на русской Windows двойно кодируют сообщения из WinHTTP:
        // байты UTF-8 кириллицы трактуются как Latin-1 и снова кодируются в UTF-8, давая "ÐÑÐµÐ²Ð¼Ñ…"
        // Признак: символы Ð/Ñ (U+00D0/D1) рядом с C1-управляющими (U+0080–U+009F).
        private static string TryFixMojibake(string s)
        {
            if (!s.Any(c => (int)c == 0xD0 || (int)c == 0xD1)) return s;
            if (!s.Any(c => (int)c >= 0x80 && (int)c <= 0x9F)) return s;
            try
            {
                byte[] bytes   = System.Text.Encoding.Latin1.GetBytes(s);
                string decoded = System.Text.Encoding.UTF8.GetString(bytes);
                return decoded.Any(c => (int)c >= 0x0400 && (int)c <= 0x04FF) ? decoded : s;
            }
            catch { return s; }
        }

        // Запуск winget, захват stdout целиком. Таймаут по умолчанию — 120 с.
        // Возвращает код выхода и вывод. ExitCode = -1, если процесс убит по таймауту.
        public static async Task<(int ExitCode, string Output)> RunAsync(string args, TimeSpan? timeout = null)
        {
            ValidateArgs(args);
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
            int exitCode = -1;
            try
            {
                await p.WaitForExitAsync(cts.Token);
                exitCode = p.ExitCode;
            }
            catch (OperationCanceledException)
            {
                // Kill(true) завершает всё дерево процессов: winget порождает дочерние
                // процессы, без них пайп не закрывается и ReadToEndAsync зависает.
                try { p.Kill(true); } catch { }
            }
            string output = await outputTask;
            await stderrTask;
            return (exitCode, output);
        }

        // Построчный стриминг вывода winget с фильтрацией прогресс-бара и ANSI.
        // Таймаут по умолчанию — 45 мин (upgrade --all).
        public static async Task<int> RunStreamingAsync(string args, Action<string> onLine, TimeSpan? timeout = null)
        {
            ValidateArgs(args);
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
                    string clean = TryFixMojibake(StripAnsi(raw).Trim());
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