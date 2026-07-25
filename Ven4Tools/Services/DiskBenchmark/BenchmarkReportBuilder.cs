using System;
using System.Globalization;
using System.Text;
using Ven4Tools.Models;

namespace Ven4Tools.Services.DiskBenchmark
{
    /// <summary>
    /// Собирает текстовый отчёт о прогоне.
    ///
    /// Числа форматируются в культуре ru-RU явно, чтобы отчёт выглядел одинаково независимо
    /// от языковых настроек машины.
    /// </summary>
    public static class BenchmarkReportBuilder
    {
        private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("ru-RU");

        // Ширины колонок таблицы результатов и суммарная ширина разделителя.
        private const int NameColumn = 14;
        private const int SpeedColumn = 14;
        private const int IopsColumn = 10;
        private const int LatencyColumn = 12;
        private const int TableWidth = NameColumn + 2 * (SpeedColumn + IopsColumn + LatencyColumn);

        public static string Build(BenchmarkRunResult result)
        {
            var report = new StringBuilder();
            var disk = result.Disk;

            report.AppendLine("Ven4Tools — тест скорости диска");
            report.AppendLine("Дата: " + result.StartedAt.ToString("dd.MM.yyyy HH:mm", Culture));
            report.AppendLine();

            if (disk != null)
            {
                report.AppendLine("Накопитель:     " + disk.FriendlyName);
                report.AppendLine("Объём:          " + FormatCapacity(disk.SizeBytes));
                report.AppendLine("Тип носителя:   " + DescribeMediaWithSpindle(disk));
                report.AppendLine("Подключение:    " + DescribeConnection(disk));

                if (disk.Link.IsKnown)
                {
                    report.AppendLine("Потолок шины:   " +
                        disk.Link.CeilingMegabytesPerSecond.ToString("F0", Culture) + " МБ/с");
                }
            }

            report.AppendLine("Тестовый том:   " + result.VolumeLetter);
            report.AppendLine("Профиль:        " + DiskBenchmarkEngine.DescribeProfile(result.Profile) +
                              ", проходов: " + result.Passes.ToString(Culture));
            report.AppendLine("Тестовый файл:  " + FormatBinarySize(result.FileSizeBytes));
            report.AppendLine("Длительность:   " + FormatDuration(result.Duration));

            if (result.Cancelled)
            {
                report.AppendLine();
                report.AppendLine("ВНИМАНИЕ: тест остановлен пользователем, результаты неполные.");
            }

            report.AppendLine();
            report.AppendLine("Результаты");
            report.AppendLine(new string('-', TableWidth));
            report.AppendLine(
                "Тест".PadRight(NameColumn) +
                "Чтение, МБ/с".PadLeft(SpeedColumn) + "IOPS".PadLeft(IopsColumn) + "Задержка".PadLeft(LatencyColumn) +
                "Запись, МБ/с".PadLeft(SpeedColumn) + "IOPS".PadLeft(IopsColumn) + "Задержка".PadLeft(LatencyColumn));
            report.AppendLine(new string('-', TableWidth));

            foreach (var pattern in DiskBenchmarkEngine.Patterns)
            {
                var read = result.Find(pattern.Name, BenchmarkOperation.Read);
                var write = result.Find(pattern.Name, BenchmarkOperation.Write);

                report.AppendLine(
                    pattern.Name.PadRight(NameColumn) +
                    FormatSpeed(read).PadLeft(SpeedColumn) +
                    FormatIops(read).PadLeft(IopsColumn) +
                    FormatLatency(read).PadLeft(LatencyColumn) +
                    FormatSpeed(write).PadLeft(SpeedColumn) +
                    FormatIops(write).PadLeft(IopsColumn) +
                    FormatLatency(write).PadLeft(LatencyColumn));
            }

            report.AppendLine(new string('-', TableWidth));
            report.AppendLine();
            report.AppendLine("Выводы");

            var sequentialRead = result.Find("SEQ1M Q8T1", BenchmarkOperation.Read);
            if (sequentialRead != null)
            {
                report.AppendLine("• Последовательное чтение " +
                    sequentialRead.MegabytesPerSecond.ToString("F0", Culture) + " МБ/с — " +
                    DescribeLevel(sequentialRead.MegabytesPerSecond) + ".");

                if (disk != null && disk.Link.IsKnown && disk.Link.CeilingMegabytesPerSecond > 0)
                {
                    double share = sequentialRead.MegabytesPerSecond / disk.Link.CeilingMegabytesPerSecond * 100;
                    report.AppendLine("• Накопитель выбирает " + share.ToString("F0", Culture) +
                        "% пропускной способности интерфейса.");
                }
            }

            var randomRead = result.Find("RND4K Q1T1", BenchmarkOperation.Read);
            if (randomRead != null && randomRead.AverageLatencyMicroseconds > 0)
            {
                report.AppendLine("• Задержка одиночного случайного чтения — " +
                    FormatLatency(randomRead) + ". От неё зависит отзывчивость системы " +
                    "и скорость запуска программ сильнее, чем от последовательной скорости.");
            }

            if (result.Warnings.Count > 0)
            {
                report.AppendLine();
                report.AppendLine("Что могло повлиять на результат");
                foreach (string warning in result.Warnings)
                    report.AppendLine("• " + warning);
            }

            report.AppendLine();
            report.AppendLine("Как измерено");
            report.AppendLine("• Через временный файл на выбранном томе, полностью в обход кэша " +
                              "файловой системы. Файл удалён после теста.");
            report.AppendLine("• Задержка вычислена из пропускной способности и глубины очереди " +
                              "по закону Литтла, а не измерена отдельно по каждой операции.");
            report.AppendLine("• Паттерны с очередью 1 измеряются синхронным вводом-выводом: на " +
                              "такой глубине накладные расходы асинхронного пути попали бы в " +
                              "результат целиком и занизили бы его почти вдвое.");
            report.AppendLine("• Сравнивая с другими программами, учитывайте, что совпадение " +
                              "подписи паттерна не гарантирует одинаковую фактически достигнутую " +
                              "глубину очереди.");

            return report.ToString();
        }

        /// <summary>
        /// Описывает подключение. Там, где параметры интерфейса недостижимы, честно
        /// сообщает об этом вместо правдоподобной подстановки.
        /// </summary>
        public static string DescribeConnection(PhysicalDiskInfo disk)
        {
            string bus = DiskInventoryService.DescribeBus(disk.Bus);

            if (disk.Bus == DiskBusKind.Nvme)
            {
                return disk.Link.IsKnown
                    ? bus + ", PCIe " + disk.Link.Generation.ToString(Culture) + ".0 x" +
                      disk.Link.Width.ToString(Culture)
                    : bus + " (поколение и число линий не определяются)";
            }

            if (disk.Bus == DiskBusKind.Sata || disk.Bus == DiskBusKind.Ata)
                return bus + " (ревизия интерфейса не определяется)";

            if (disk.Bus == DiskBusKind.Usb)
                return bus + " (поколение интерфейса не определяется)";

            return bus;
        }

        /// <summary>
        /// Относит результат к классу накопителей. Формулировка намеренно про сопоставимость,
        /// а не про факт: скорость зависит от условий замера, а не только от железа.
        /// </summary>
        public static string DescribeLevel(double sequentialReadMegabytesPerSecond) =>
            sequentialReadMegabytesPerSecond switch
            {
                < 200 => "сопоставимо с уровнем жёсткого диска",
                < 600 => "сопоставимо с уровнем SATA SSD",
                // Граница проходит по практическому потолку каждого поколения на четырёх
                // линиях: около 3,9 ГБ/с у PCIe 3.0 и около 7,9 ГБ/с у PCIe 4.0.
                < 4000 => "сопоставимо с уровнем NVMe PCIe 3.0",
                < 7500 => "сопоставимо с уровнем NVMe PCIe 4.0",
                _ => "сопоставимо с уровнем NVMe PCIe 5.0"
            };

        private static string DescribeMediaWithSpindle(PhysicalDiskInfo disk)
        {
            string media = DiskInventoryService.DescribeMedia(disk.Media);
            return disk.SpindleSpeed > 0
                ? media + ", " + disk.SpindleSpeed.ToString(Culture) + " об/мин"
                : media;
        }

        private static string FormatSpeed(BenchmarkMeasurement? measurement) =>
            measurement == null ? "—" : measurement.MegabytesPerSecond.ToString("F1", Culture);

        private static string FormatIops(BenchmarkMeasurement? measurement) =>
            measurement == null ? "—" : measurement.OperationsPerSecond.ToString("F0", Culture);

        private static string FormatLatency(BenchmarkMeasurement? measurement)
        {
            if (measurement == null || measurement.AverageLatencyMicroseconds <= 0) return "—";
            double microseconds = measurement.AverageLatencyMicroseconds;
            return microseconds >= 1000
                ? (microseconds / 1000).ToString("F2", Culture) + " мс"
                : microseconds.ToString("F0", Culture) + " мкс";
        }

        /// <summary>Ёмкость в десятичных единицах — так её указывают производители.</summary>
        private static string FormatCapacity(long bytes)
        {
            if (bytes >= 1_000_000_000_000L)
                return (bytes / 1_000_000_000_000d).ToString("F2", Culture) + " ТБ";
            return (bytes / 1_000_000_000d).ToString("F1", Culture) + " ГБ";
        }

        /// <summary>Размер файла в двоичных единицах — так его задаёт пользователь.</summary>
        public static string FormatBinarySize(long bytes) =>
            (bytes / 1024d / 1024 / 1024).ToString("F0", Culture) + " ГиБ";

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalMinutes >= 1)
                return ((int)duration.TotalMinutes).ToString(Culture) + " мин " +
                       duration.Seconds.ToString(Culture) + " с";
            return duration.TotalSeconds.ToString("F0", Culture) + " с";
        }
    }
}
