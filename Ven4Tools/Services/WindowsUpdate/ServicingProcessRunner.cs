using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services.WindowsUpdate
{
    /// <summary>Итог запуска dism.exe / sfc.exe.</summary>
    public sealed class ServicingProcessResult
    {
        public int ExitCode { get; init; }
        public string Output { get; init; } = "";
        public bool Cancelled { get; init; }
        /// <summary>Процесс не запустился вовсе (файла нет, отказ системы).</summary>
        public string StartError { get; init; } = "";
    }

    /// <summary>
    /// Разбор консольного вывода утилит обслуживания Windows.
    /// <para>
    /// sfc.exe при перенаправлении вывода пишет в UTF-16LE (без метки порядка байт),
    /// dism.exe — в OEM-кодировке консоли (на русской Windows это 866). Угадать заранее
    /// нельзя, поэтому кодировка определяется по первым байтам: в UTF-16LE почти каждый
    /// второй байт — 0x00 (латиница, цифры) или 0x04 (кириллица). Прогресс обе утилиты
    /// перерисовывают в той же строке через возврат каретки, поэтому строки режутся и
    /// по \r, и по \n.
    /// </para>
    /// </summary>
    public static class ConsoleOutputDecoder
    {
        private static readonly Regex PercentRegex = new(
            @"(\d{1,3}(?:[.,]\d+)?)\s*%", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        static ConsoleOutputDecoder()
        {
            // 866 и прочие OEM-кодовые страницы в .NET доступны только через провайдер.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        /// <summary>true — первые байты похожи на UTF-16LE.</summary>
        public static bool LooksLikeUtf16Le(ReadOnlySpan<byte> head)
        {
            if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE) return true;
            int pairs = Math.Min(head.Length / 2, 256);
            if (pairs < 2) return false;
            // Старший байт символа UTF-16LE у латиницы, цифр и знаков — 0x00, у кириллицы —
            // 0x04. В однобайтовой OEM-кодировке на нечётных местах стоят обычные буквы и
            // знаки (0x20 и выше): байты 0x00 и 0x04 там — управляющие и не встречаются.
            int utf16High = 0;
            for (int i = 0; i < pairs; i++)
                if (head[i * 2 + 1] is 0x00 or 0x04) utf16High++;
            return utf16High * 10 >= pairs * 8; // 80% пар и больше
        }

        /// <summary>Кодировка вывода: UTF-16LE или OEM-кодировка системы.</summary>
        public static Encoding DetectEncoding(ReadOnlySpan<byte> head) =>
            LooksLikeUtf16Le(head) ? new UnicodeEncoding(false, false) : OemEncoding();

        public static Encoding OemEncoding()
        {
            try { return Encoding.GetEncoding((int)GetOEMCP()); }
            catch (Exception)
            {
                try { return Encoding.GetEncoding(CultureInfo.InstalledUICulture.TextInfo.OEMCodePage); }
                catch (Exception) { return Encoding.ASCII; }
            }
        }

        /// <summary>Декодирует буфер целиком (для stderr и тестов).</summary>
        public static string Decode(byte[] bytes)
        {
            if (bytes.Length == 0) return "";
            var encoding = DetectEncoding(bytes);
            string text = encoding.GetString(bytes);
            return text.TrimStart('\uFEFF');
        }

        /// <summary>
        /// Последний процент в строке вывода: «[====  45.2% ]», «Verification 45% complete.»,
        /// «Проверка 45% завершена.». Значения больше 100 отбрасываются.
        /// </summary>
        public static bool TryParsePercent(string line, out double percent)
        {
            percent = 0;
            var matches = PercentRegex.Matches(line);
            if (matches.Count == 0) return false;
            string raw = matches[^1].Groups[1].Value.Replace(',', '.');
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return false;
            if (value < 0 || value > 100) return false;
            percent = value;
            return true;
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();
    }

    /// <summary>
    /// Запуск dism.exe / sfc.exe без окна, с потоковым разбором вывода (проценты в
    /// прогресс), отменой и полным текстом вывода в итоге.
    /// </summary>
    public static class ServicingProcessRunner
    {
        /// <summary>%SystemRoot%\System32\dism.exe — по полному пути (см. TrustedExecutablePaths).</summary>
        public static string DismExe { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");

        /// <summary>%SystemRoot%\System32\sfc.exe.</summary>
        public static string SfcExe { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sfc.exe");

        /// <summary>
        /// Имена процессов, с которыми одновременно обслуживание образа не выполняется:
        /// второй DISM/SFC либо ждёт, либо падает с невнятной ошибкой.
        /// </summary>
        private static readonly string[] ServicingProcessNames = { "dism", "DismHost", "sfc" };

        /// <summary>Имя уже запущенной утилиты обслуживания или null.</summary>
        public static string? FindRunningServicingTool()
        {
            foreach (var name in ServicingProcessNames)
            {
                Process[] found;
                try { found = Process.GetProcessesByName(name); }
                catch (Exception) { continue; }
                try
                {
                    if (found.Length > 0) return name + ".exe";
                }
                finally
                {
                    foreach (var p in found) p.Dispose();
                }
            }
            return null;
        }

        public static async Task<ServicingProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            Action<double>? onPercent,
            CancellationToken ct,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var a in arguments) psi.ArgumentList.Add(a);
            if (environment != null)
                foreach (var pair in environment) psi.Environment[pair.Key] = pair.Value;

            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Обслуживание] Не удалось запустить {Path.GetFileName(fileName)}: {ex.Message}");
                return new ServicingProcessResult { ExitCode = -1, StartError = ex.Message };
            }
            if (process == null)
                return new ServicingProcessResult { ExitCode = -1, StartError = "процесс не запустился" };

            using (process)
            {
                var output = new StringBuilder();
                var stdoutTask = Task.Run(() => PumpOutput(process.StandardOutput.BaseStream, output, onPercent));
                var stderrTask = Task.Run(async () =>
                {
                    using var ms = new MemoryStream();
                    await process.StandardError.BaseStream.CopyToAsync(ms);
                    return ConsoleOutputDecoder.Decode(ms.ToArray());
                });

                bool cancelled = false;
                try
                {
                    await process.WaitForExitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception ex) { AppLogger.Write($"[Обслуживание] Остановка {Path.GetFileName(fileName)}: {ex.Message}"); }
                    try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)); }
                    catch (Exception) { /* процесс не закрылся за 30 с — итог всё равно «отменено» */ }
                }

                try { await stdoutTask.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
                string err = "";
                try { err = await stderrTask.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }

                string text;
                lock (output) text = output.ToString();
                if (!string.IsNullOrWhiteSpace(err)) text += Environment.NewLine + err.Trim();

                int exitCode = -1;
                try { if (process.HasExited) exitCode = process.ExitCode; } catch (Exception) { }

                return new ServicingProcessResult { ExitCode = exitCode, Output = text, Cancelled = cancelled };
            }
        }

        /// <summary>
        /// Читает stdout кусками: кодировка определяется по первому куску, дальше —
        /// потоковый декодер (символ UTF-16 может разорваться между кусками). Каждый
        /// фрагмент между \r и \n проверяется на процент.
        /// </summary>
        private static void PumpOutput(Stream stream, StringBuilder output, Action<double>? onPercent)
        {
            var buffer = new byte[4096];
            var chars = new char[8192];
            Decoder? decoder = null;
            // Пока байтов мало, кодировку не определяем: по одному-двум байтам UTF-16 от
            // OEM не отличить. Копим первые байты и решаем, когда их наберётся достаточно.
            var head = new List<byte>();
            const int headNeeded = 16;
            var line = new StringBuilder();
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (decoder == null)
                {
                    head.AddRange(buffer.AsSpan(0, read).ToArray());
                    if (head.Count < headNeeded) continue;
                    var headBytes = head.ToArray();
                    decoder = ConsoleOutputDecoder.DetectEncoding(headBytes).GetDecoder();
                    Consume(decoder, headBytes, headBytes.Length, chars, output, line, onPercent);
                    continue;
                }
                Consume(decoder, buffer, read, chars, output, line, onPercent);
            }
            if (decoder == null && head.Count > 0)
            {
                var headBytes = head.ToArray();
                decoder = ConsoleOutputDecoder.DetectEncoding(headBytes).GetDecoder();
                Consume(decoder, headBytes, headBytes.Length, chars, output, line, onPercent);
            }
            Flush(line, onPercent);
        }

        private static void Consume(Decoder decoder, byte[] bytes, int length, char[] chars,
            StringBuilder output, StringBuilder line, Action<double>? onPercent)
        {
            int offset = 0;
            while (offset < length)
            {
                // Кусками, чтобы буфер символов не переполнился.
                int take = Math.Min(length - offset, 4096);
                int count = decoder.GetChars(bytes, offset, take, chars, 0);
                offset += take;
                for (int i = 0; i < count; i++)
                {
                    char c = chars[i];
                    if (c == '\uFEFF') continue;
                    lock (output) output.Append(c);
                    if (c == '\r' || c == '\n')
                    {
                        Flush(line, onPercent);
                        continue;
                    }
                    line.Append(c);
                }
            }
            // Строка прогресса DISM может долго не закрываться переводом строки —
            // проверяем и незавершённый хвост.
            if (line.Length > 0 && line.ToString().Contains('%'))
                Report(line.ToString(), onPercent);
        }

        private static void Flush(StringBuilder line, Action<double>? onPercent)
        {
            if (line.Length == 0) return;
            Report(line.ToString(), onPercent);
            line.Clear();
        }

        private static void Report(string text, Action<double>? onPercent)
        {
            if (onPercent != null && ConsoleOutputDecoder.TryParsePercent(text, out double p))
            {
                try { onPercent(p); } catch (Exception) { }
            }
        }
    }
}
