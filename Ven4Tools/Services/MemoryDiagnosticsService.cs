using System;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    /// <summary>Чем закончился тест памяти.</summary>
    internal enum MemoryTestOutcome { NoErrors, ErrorsFound, Interrupted, NotCompleted }

    /// <summary>Итог последнего штатного теста памяти из журнала System.</summary>
    internal sealed record MemoryTestResult(DateTime When, MemoryTestOutcome Outcome)
    {
        public bool ErrorsFound => Outcome == MemoryTestOutcome.ErrorsFound;
    }

    /// <summary>
    /// «Проверка оперативной памяти» штатным средством Windows (mdsched.exe). Сам тест
    /// идёт при перезагрузке; Ven4Tools только запускает окно Windows, где человек
    /// выбирает «перезагрузить сейчас» или «при следующем включении», а потом читает итог
    /// из журнала System (источник Microsoft-Windows-MemoryDiagnostics-Results).
    /// </summary>
    internal static class MemoryDiagnosticsService
    {
        internal const string ResultsProvider = "Microsoft-Windows-MemoryDiagnostics-Results";
        // Номера событий источника (Get-WinEvent -ListProvider): 1101/1201 — ошибок нет,
        // 1102/1202 — найдены аппаратные ошибки, 1103 — тест прерван, 1104 — не завершён.
        // Пары 11xx и 12xx значат одно и то же — Windows пишет то одну, то другую.
        private static readonly int[] KnownEventIds = { 1101, 1102, 1103, 1104, 1201, 1202 };

        public static string MdschedPath => Path.Combine(Environment.SystemDirectory, "mdsched.exe");

        /// <summary>Открыть штатное окно «Средство проверки памяти Windows». Перезагрузку предлагает оно само.</summary>
        public static bool Launch()
        {
            try
            {
                if (!File.Exists(MdschedPath))
                {
                    AppLogger.Write("🧠 mdsched.exe не найден в System32");
                    return false;
                }
                Process.Start(new ProcessStartInfo(MdschedPath) { UseShellExecute = true });
                AppLogger.Write("🧠 Открыто средство проверки памяти Windows (mdsched.exe)");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "MemoryDiagnosticsService.Launch");
                return false;
            }
        }

        /// <summary>Последний итог теста памяти или null — тест не проводился (или журнал очищен).</summary>
        public static Task<MemoryTestResult?> ReadLastResultAsync() =>
            Task.Run(() =>
            {
                try
                {
                    string ids = string.Join(" or ", Array.ConvertAll(KnownEventIds, id => $"EventID={id}"));
                    string filter = $"*[System[Provider[@Name='{ResultsProvider}'] and ({ids})]]";
                    var query = new EventLogQuery("System", PathType.LogName, filter) { ReverseDirection = true };
                    using var reader = new EventLogReader(query);
                    using var record = reader.ReadEvent();
                    if (record?.TimeCreated == null) return null;
                    return FromEvent(record.Id, record.TimeCreated.Value);
                }
                catch (Exception ex)
                {
                    AppLogger.Write(ex, "MemoryDiagnosticsService.ReadLastResult");
                    return null;
                }
            });

        /// <summary>Событие журнала → итог. Чужие номера событий — null.</summary>
        internal static MemoryTestResult? FromEvent(int eventId, DateTime when) => eventId switch
        {
            1101 or 1201 => new MemoryTestResult(when, MemoryTestOutcome.NoErrors),
            1102 or 1202 => new MemoryTestResult(when, MemoryTestOutcome.ErrorsFound),
            1103 => new MemoryTestResult(when, MemoryTestOutcome.Interrupted),
            1104 => new MemoryTestResult(when, MemoryTestOutcome.NotCompleted),
            _ => null
        };

        /// <summary>Строка для показа и отчёта.</summary>
        internal static string Describe(MemoryTestResult? result)
        {
            if (result == null) return "Тест не проводился";
            string when = result.When.ToString("g", CultureInfo.CurrentCulture);
            return result.Outcome switch
            {
                MemoryTestOutcome.NoErrors => $"Последний тест {when}: ошибок не обнаружено",
                MemoryTestOutcome.ErrorsFound => $"Последний тест {when}: обнаружены ошибки памяти — стоит проверить модули памяти и отключить разгон (XMP/EXPO), если он включён",
                MemoryTestOutcome.Interrupted => $"Последний тест {when} был прерван — итога нет, проверку можно запустить снова",
                _ => $"Последний тест {when} не удалось завершить — итога нет, проверку можно запустить снова"
            };
        }
    }
}
