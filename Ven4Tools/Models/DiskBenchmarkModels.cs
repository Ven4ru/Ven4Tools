using System;
using System.Collections.Generic;

namespace Ven4Tools.Models
{
    /// <summary>Тип шины, по которой подключён накопитель. Значения соответствуют BusType из MSFT_PhysicalDisk.</summary>
    public enum DiskBusKind
    {
        Unknown,
        Scsi,
        Atapi,
        Ata,
        Ieee1394,
        Ssa,
        FibreChannel,
        Usb,
        Raid,
        IScsi,
        Sas,
        Sata,
        Sd,
        Mmc,
        Virtual,
        FileBackedVirtual,
        StorageSpaces,
        Nvme,
        Scm,
        Ufs
    }

    /// <summary>Тип носителя. Значения соответствуют MediaType из MSFT_PhysicalDisk.</summary>
    public enum DiskMediaKind
    {
        Unknown,
        Hdd,
        Ssd,
        Scm
    }

    /// <summary>
    /// Параметры линии PCIe, по которой подключён накопитель.
    /// Определяются не всегда — при неудаче остаются нулевыми, и тогда потолок интерфейса
    /// не считается и наверх не выводится. Правдоподобные значения не подставляются никогда.
    /// </summary>
    public sealed class PciLinkInfo
    {
        /// <summary>Линия с неопределёнными параметрами.</summary>
        public static readonly PciLinkInfo Unknown = new PciLinkInfo();

        /// <summary>Поколение PCIe: 1..5. Ноль означает «не определено».</summary>
        public int Generation { get; init; }

        /// <summary>Число линий. Ноль означает «не определено».</summary>
        public int Width { get; init; }

        /// <summary>Оба параметра получены и укладываются в известный диапазон.</summary>
        public bool IsKnown => Generation >= 1 && Generation <= 5 && Width > 0;

        /// <summary>Полезная пропускная способность одной линии в МБ/с с учётом кодирования.</summary>
        private static double LaneThroughput(int generation) => generation switch
        {
            1 => 250,
            2 => 500,
            3 => 985,
            4 => 1969,
            5 => 3938,
            _ => 0
        };

        /// <summary>Теоретический потолок интерфейса в МБ/с. Ноль означает «не определён».</summary>
        public double CeilingMegabytesPerSecond => IsKnown ? LaneThroughput(Generation) * Width : 0;
    }

    /// <summary>Логический том, пригодный или непригодный для запуска теста.</summary>
    public sealed class BenchmarkVolumeInfo
    {
        /// <summary>Буква тома с двоеточием, например «C:».</summary>
        public string Letter { get; init; } = "";

        public string Label { get; init; } = "";
        public long TotalBytes { get; init; }
        public long FreeBytes { get; init; }
        public bool IsReady { get; init; }
        public bool IsSystem { get; init; }
        public bool IsRemovable { get; init; }

        /// <summary>Доля занятого места в процентах.</summary>
        public double UsedPercent => TotalBytes > 0 ? (TotalBytes - FreeBytes) * 100.0 / TotalBytes : 0;

        public override string ToString() =>
            string.IsNullOrWhiteSpace(Label) ? Letter : Letter + " (" + Label + ")";
    }

    /// <summary>Физический накопитель системы.</summary>
    public sealed class PhysicalDiskInfo
    {
        public uint Index { get; init; }
        public string FriendlyName { get; init; } = "";
        public long SizeBytes { get; init; }
        public DiskBusKind Bus { get; init; }
        public DiskMediaKind Media { get; init; }

        /// <summary>Обороты шпинделя. Ноль означает «не применимо или не определено».</summary>
        public uint SpindleSpeed { get; init; }

        public PciLinkInfo Link { get; set; } = PciLinkInfo.Unknown;
        public List<BenchmarkVolumeInfo> Volumes { get; } = new List<BenchmarkVolumeInfo>();

        /// <summary>Есть хотя бы один том, на котором можно разместить тестовый файл.</summary>
        public bool CanBenchmark
        {
            get
            {
                foreach (var volume in Volumes)
                {
                    if (volume.IsReady) return true;
                }
                return false;
            }
        }
    }

    /// <summary>Паттерн нагрузки: размер блока, глубина очереди, число потоков запросов, характер доступа.</summary>
    public sealed class BenchmarkPattern
    {
        public string Name { get; init; } = "";
        public int BlockSize { get; init; }
        public int QueueDepth { get; init; }
        public int ThreadCount { get; init; }
        public bool Sequential { get; init; }

        /// <summary>Сколько операций держится незавершёнными одновременно.</summary>
        public int Streams => QueueDepth * ThreadCount;
    }

    public enum BenchmarkOperation
    {
        Read,
        Write
    }

    /// <summary>Результат одного замера: паттерн, направление и три величины.</summary>
    public sealed class BenchmarkMeasurement
    {
        public string PatternName { get; init; } = "";
        public BenchmarkOperation Operation { get; init; }
        public double MegabytesPerSecond { get; init; }
        public double OperationsPerSecond { get; init; }
        public double AverageLatencyMicroseconds { get; init; }
    }

    /// <summary>Профиль прогона задаёт число проходов.</summary>
    public enum BenchmarkProfile
    {
        Fast,
        Normal,
        Precise
    }

    /// <summary>Ход выполнения прогона для отображения в интерфейсе.</summary>
    public sealed class BenchmarkProgress
    {
        public string Stage { get; init; } = "";

        /// <summary>Доля выполненного от нуля до единицы.</summary>
        public double Fraction { get; init; }
    }

    /// <summary>Полный результат прогона.</summary>
    public sealed class BenchmarkRunResult
    {
        public PhysicalDiskInfo? Disk { get; init; }
        public string VolumeLetter { get; init; } = "";
        public BenchmarkProfile Profile { get; init; }
        public int Passes { get; init; }
        public long FileSizeBytes { get; init; }
        public DateTime StartedAt { get; init; }
        public TimeSpan Duration { get; set; }
        public bool Cancelled { get; set; }
        public List<BenchmarkMeasurement> Measurements { get; } = new List<BenchmarkMeasurement>();
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>Замер по паттерну и направлению, если он успел выполниться.</summary>
        public BenchmarkMeasurement? Find(string patternName, BenchmarkOperation operation)
        {
            foreach (var measurement in Measurements)
            {
                if (measurement.PatternName == patternName && measurement.Operation == operation)
                    return measurement;
            }
            return null;
        }
    }
}
