using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services.WindowsUpdate
{
    /// <summary>Итог sfc /scannow.</summary>
    public enum SfcVerdict
    {
        /// <summary>Итог не распознан — смотреть CBS.log.</summary>
        Unknown,
        NoViolations,
        Repaired,
        /// <summary>Нашлись повреждения, часть исправить не удалось.</summary>
        NotAllRepaired,
        /// <summary>Проверку выполнить не удалось.</summary>
        CouldNotRun,
        /// <summary>Ждёт перезагрузки незавершённое восстановление — сначала перезагрузка.</summary>
        RebootPending
    }

    /// <summary>
    /// Чистая логика итога sfc. Текст итога sfc пишет на языке системы, поэтому
    /// сначала ищутся известные фразы (английские и русские), а если не нашлись —
    /// строки с меткой [SR] из CBS.log, дописанные за время проверки: журнал CBS
    /// всегда по-английски.
    /// </summary>
    public static class SfcResultClassifier
    {
        public static SfcVerdict Classify(string sfcOutput, string cbsAppended)
        {
            string text = Normalize(sfcOutput);

            if (Has(text, "did not find any integrity violations", "не обнаружила нарушений целостности",
                    "не обнаружила нарушения целостности"))
                return SfcVerdict.NoViolations;
            if (Has(text, "unable to fix some", "не может восстановить некоторые", "не удалось восстановить некоторые",
                    "но не может восстановить"))
                return SfcVerdict.NotAllRepaired;
            if (Has(text, "successfully repaired", "успешно их восстановила", "успешно восстановила"))
                return SfcVerdict.Repaired;
            if (Has(text, "could not perform the requested operation", "не удается выполнить запрошенную операцию",
                    "не удаётся выполнить запрошенную операцию", "не может выполнить запрошенную операцию"))
                return SfcVerdict.CouldNotRun;

            // Проверяется последним: в остальных итогах тоже может встретиться слово
            // «перезагрузка», а этот — единственный, где проверка вообще не начиналась.
            if (Has(text, "repair pending", "requires reboot to complete", "ожидает перезагрузки",
                    "незавершенное восстановление", "незавершённое восстановление", "ожидается восстановление"))
                return SfcVerdict.RebootPending;

            // Запасной путь — CBS.log (только строки этой проверки).
            string cbs = cbsAppended ?? "";
            var srLines = cbs.Split('\n').Where(l => l.Contains("[SR]", StringComparison.Ordinal)).ToList();
            if (srLines.Count == 0) return SfcVerdict.Unknown;
            if (srLines.Any(l => l.Contains("Cannot repair member file", StringComparison.OrdinalIgnoreCase)))
                return SfcVerdict.NotAllRepaired;
            if (srLines.Any(l => l.Contains("Repaired file", StringComparison.OrdinalIgnoreCase)
                              || l.Contains("Repairing corrupted file", StringComparison.OrdinalIgnoreCase)))
                return SfcVerdict.Repaired;
            if (srLines.Any(l => l.Contains("Verify complete", StringComparison.OrdinalIgnoreCase)))
                return SfcVerdict.NoViolations;
            return SfcVerdict.Unknown;
        }

        /// <summary>Итог по-русски для окна результата.</summary>
        public static string Describe(SfcVerdict verdict) => verdict switch
        {
            SfcVerdict.NoViolations => "Проверка системных файлов: нарушений целостности не найдено.",
            SfcVerdict.Repaired => "Проверка системных файлов: найдены повреждённые файлы, они восстановлены. Перезагрузите компьютер.",
            SfcVerdict.NotAllRepaired => "Проверка системных файлов: найдены повреждённые файлы, часть восстановить не удалось. Подробности — в журнале " + SystemFileRepairService.CbsLogPath + ".",
            SfcVerdict.CouldNotRun => "Проверку системных файлов выполнить не удалось. Попробуйте после перезагрузки; подробности — в журнале " + SystemFileRepairService.CbsLogPath + ".",
            SfcVerdict.RebootPending => "Windows ждёт перезагрузки, чтобы завершить прошлое восстановление. Перезагрузите компьютер и запустите проверку ещё раз.",
            _ => "Итог проверки системных файлов распознать не удалось. Подробности — в журнале " + SystemFileRepairService.CbsLogPath + "."
        };

        private static string Normalize(string s) =>
            (s ?? "").Replace('\0', ' ').Replace("\r", " ").Replace("\n", " ").ToLowerInvariant();

        private static bool Has(string text, params string[] phrases) =>
            phrases.Any(p => text.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// «Проверить системные файлы»: DISM /Online /Cleanup-Image /RestoreHealth (чинит
    /// хранилище компонентов, из которого sfc берёт исправные копии), затем sfc /scannow.
    /// Долго — до получаса и дольше — и помогает не всегда: если хранилище повреждено,
    /// а Центр обновления исправные файлы не отдал, sfc восстанавливать будет не из чего.
    /// </summary>
    public static class SystemFileRepairService
    {
        public static string CbsLogPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "CBS", "CBS.log");

        public static string DismLogPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "DISM", "dism.log");

        public static async Task<DismOperationResult> RunRestoreHealthAsync(Action<double>? onPercent, CancellationToken ct)
        {
            var run = await ServicingProcessRunner.RunAsync(ServicingProcessRunner.DismExe,
                new[] { "/Online", "/English", "/Cleanup-Image", "/RestoreHealth" }, onPercent, ct);
            var result = DismOperationResult.From(run);
            AppLogger.Write(run.Cancelled
                ? "[Ремонт] DISM /RestoreHealth: отменено пользователем"
                : result.Success
                    ? "[Ремонт] DISM /RestoreHealth: завершено успешно"
                    : $"[Ремонт] DISM /RestoreHealth: ошибка 0x{result.ErrorCode:X8}");
            return result;
        }

        public static async Task<(SfcVerdict Verdict, bool Cancelled, string Error)> RunSfcAsync(
            Action<double>? onPercent, CancellationToken ct)
        {
            long cbsStart = SafeLength(CbsLogPath);
            var run = await ServicingProcessRunner.RunAsync(ServicingProcessRunner.SfcExe,
                new[] { "/scannow" }, onPercent, ct);
            if (!string.IsNullOrEmpty(run.StartError))
                return (SfcVerdict.CouldNotRun, false, $"Не удалось запустить sfc: {run.StartError}");
            if (run.Cancelled)
            {
                AppLogger.Write("[Ремонт] sfc /scannow: отменено пользователем");
                return (SfcVerdict.Unknown, true, "");
            }

            string cbsAppended = ReadAppended(CbsLogPath, cbsStart);
            var verdict = SfcResultClassifier.Classify(run.Output, cbsAppended);
            AppLogger.Write($"[Ремонт] sfc /scannow: код {run.ExitCode}, итог — {verdict}");
            return (verdict, false, "");
        }

        private static long SafeLength(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch (Exception) { return 0; }
        }

        /// <summary>
        /// Хвост CBS.log, дописанный после <paramref name="fromOffset"/>. Журнал открыт
        /// службой на запись, поэтому чтение с FileShare.ReadWrite. Если за время проверки
        /// журнал сменился (Windows архивирует большой CBS.log), читается весь новый файл.
        /// Читается не больше 16 МБ с конца — [SR]-строк там с запасом.
        /// </summary>
        private static string ReadAppended(string path, long fromOffset)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long length = fs.Length;
                long start = fromOffset <= length ? fromOffset : 0;
                const long maxBytes = 16L * 1024 * 1024;
                if (length - start > maxBytes) start = length - maxBytes;
                fs.Seek(start, SeekOrigin.Begin);
                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Ремонт] CBS.log не прочитан: {ex.Message}");
                return "";
            }
        }
    }
}
