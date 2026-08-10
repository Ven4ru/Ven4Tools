using Ven4Tools.Models;

namespace Ven4Tools.Services.DiskBenchmark
{
    /// <summary>
    /// Параметры теста скорости диска: размеры тестового файла, паттерны нагрузки,
    /// профили точности и обязательный запас свободного места.
    ///
    /// Выделено из DiskBenchmarkEngine, потому что у этих данных другая аудитория,
    /// чем у самого движка: вкладка «Бенчмарк» строит по ним выпадающие списки и
    /// колонки таблицы, BenchmarkReportBuilder — строки отчёта, BenchmarkWarningService —
    /// проверку свободного места, и ни один из них не запускает измерение. Раньше все
    /// они типозависели от движка низкоуровневого ввода-вывода только ради чтения
    /// таблицы констант.
    /// </summary>
    public static class BenchmarkPresets
    {
        /// <summary>Запас свободного места сверх размера тестового файла.</summary>
        public const long FreeSpaceReserveBytes = 1024L * 1024 * 1024;

        /// <summary>Доступные размеры тестового файла.</summary>
        public static readonly long[] FileSizes =
        {
            1024L * 1024 * 1024,
            2048L * 1024 * 1024,
            4096L * 1024 * 1024,
            8192L * 1024 * 1024
        };

        /// <summary>Паттерны нагрузки в порядке вывода в таблице результатов.</summary>
        public static readonly BenchmarkPattern[] Patterns =
        {
            new BenchmarkPattern { Name = "SEQ1M Q8T1",   BlockSize = 1024 * 1024, QueueDepth = 8,  ThreadCount = 1,  Sequential = true },
            new BenchmarkPattern { Name = "SEQ1M Q1T1",   BlockSize = 1024 * 1024, QueueDepth = 1,  ThreadCount = 1,  Sequential = true },
            new BenchmarkPattern { Name = "RND4K Q32T16", BlockSize = 4096,        QueueDepth = 32, ThreadCount = 16, Sequential = false },
            new BenchmarkPattern { Name = "RND4K Q1T1",   BlockSize = 4096,        QueueDepth = 1,  ThreadCount = 1,  Sequential = false }
        };

        /// <summary>Сколько проходов делает профиль: больше проходов — точнее результат.</summary>
        public static int PassesForProfile(BenchmarkProfile profile) => profile switch
        {
            BenchmarkProfile.Fast => 1,
            BenchmarkProfile.Precise => 5,
            _ => 3
        };

        public static string DescribeProfile(BenchmarkProfile profile) => profile switch
        {
            BenchmarkProfile.Fast => "Быстрый",
            BenchmarkProfile.Precise => "Точный",
            _ => "Обычный"
        };
    }
}
