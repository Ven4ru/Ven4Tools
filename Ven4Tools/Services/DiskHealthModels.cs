using System;
using System.Collections.Generic;
using System.Linq;

namespace Ven4Tools.Services
{
    /// <summary>Тип накопителя, как его показываем человеку.</summary>
    internal enum DiskKind { Nvme, SataSsd, Hdd, Usb, Other }

    /// <summary>Общая оценка Windows (MSFT_PhysicalDisk.HealthStatus).</summary>
    internal enum WindowsDiskStatus { Healthy, Warning, Unhealthy, Unknown }

    /// <summary>Итоговая оценка SMART по правилам библиотеки дисков (та же логика, что у CrystalDiskInfo).</summary>
    internal enum SmartDiskStatus { Unknown, Good, Caution, Bad }

    /// <summary>
    /// Что знает о физическом диске Windows: MSFT_PhysicalDisk и счётчики надёжности
    /// (MSFT_StorageReliabilityCounter, то же, что Get-StorageReliabilityCounter).
    /// Счётчики надёжности — запасной источник: на части дисков они пустые или
    /// неверные (износ 0 % у NVMe, который сам отдаёт 4 %), поэтому прямые данные SMART важнее.
    /// </summary>
    internal sealed record WindowsDiskInfo
    {
        public required int Number { get; init; }
        public required string Model { get; init; }
        /// <summary>MSFT_PhysicalDisk.BusType: 7 — USB, 11 — SATA, 17 — NVMe.</summary>
        public ushort BusType { get; init; }
        /// <summary>MSFT_PhysicalDisk.MediaType: 3 — HDD, 4 — SSD, 0 — неизвестно.</summary>
        public ushort MediaType { get; init; }
        public ulong SizeBytes { get; init; }
        public WindowsDiskStatus Status { get; init; } = WindowsDiskStatus.Unknown;

        public int? CounterTemperature { get; init; }
        public long? CounterPowerOnHours { get; init; }
        public int? CounterWear { get; init; }
        public long? CounterReadErrorsUncorrected { get; init; }
        public long? CounterWriteErrorsUncorrected { get; init; }
    }

    /// <summary>Атрибут SMART: номер и сырое значение. У NVMe номера условные — поля журнала здоровья по порядку (01…0F).</summary>
    internal readonly record struct SmartAttr(byte Id, ulong Raw);

    /// <summary>Прямые данные SMART одного диска от библиотеки датчиков (без драйвера, через IOCTL).</summary>
    internal sealed record DiskSmartData
    {
        public required int DriveNumber { get; init; }
        public required string Model { get; init; }
        public bool IsNvme { get; init; }
        public bool IsSsd { get; init; }
        public bool IsUsb { get; init; }
        public SmartDiskStatus Status { get; init; }
        public int? Temperature { get; init; }
        /// <summary>Остаток ресурса, % (100 — новый). Библиотека считает его по атрибутам конкретного производителя.</summary>
        public int? Life { get; init; }
        /// <summary>Записано хостом, ГиБ (так считает библиотека).</summary>
        public ulong? HostWritesGib { get; init; }
        public ulong PowerOnCount { get; init; }
        public ulong PowerOnHours { get; init; }
        public IReadOnlyList<SmartAttr> Attributes { get; init; } = Array.Empty<SmartAttr>();
    }

    /// <summary>Сведённые данные одного диска — то, что видит человек и по чему выносится вердикт.</summary>
    internal sealed record DiskHealthReading
    {
        public required int Number { get; init; }
        public required string Model { get; init; }
        public required DiskKind Kind { get; init; }
        public ulong SizeBytes { get; init; }

        /// <summary>Израсходованный ресурс, %. У NVMe — Percentage Used (может быть больше 100).</summary>
        public int? WearPercent { get; init; }
        public double? WrittenTb { get; init; }
        public int? TemperatureC { get; init; }
        public long? PowerOnHours { get; init; }
        public long? PowerCycles { get; init; }
        public long? UnsafeShutdowns { get; init; }

        /// <summary>NVMe: Media and Data Integrity Errors.</summary>
        public long? MediaErrors { get; init; }
        /// <summary>ATA 05: переназначенные секторы.</summary>
        public long? Reallocated { get; init; }
        /// <summary>ATA C5: ожидающие переназначения (нестабильные) секторы.</summary>
        public long? Pending { get; init; }
        /// <summary>ATA C6 (или неисправленные ошибки по счётчикам Windows): неисправимые.</summary>
        public long? Uncorrectable { get; init; }

        /// <summary>NVMe: байт Critical Warning.</summary>
        public int? NvmeCriticalWarning { get; init; }
        public int? AvailableSpare { get; init; }
        public int? AvailableSpareThreshold { get; init; }

        public WindowsDiskStatus WindowsStatus { get; init; } = WindowsDiskStatus.Unknown;
        public SmartDiskStatus SmartStatus { get; init; } = SmartDiskStatus.Unknown;

        /// <summary>Есть ли хоть один показатель, по которому можно судить о здоровье (не только температура и часы).</summary>
        public bool HasHealthData =>
            WearPercent != null || MediaErrors != null || Reallocated != null || Pending != null ||
            Uncorrectable != null || NvmeCriticalWarning != null || AvailableSpare != null;
    }

    /// <summary>
    /// Сведение данных Windows и SMART в одну строку таблицы. Чистая функция — покрыта
    /// модульными проверками. При расхождении побеждают прямые данные SMART.
    /// </summary>
    internal static class DiskHealthMerger
    {
        // Номера NVMe-полей журнала здоровья (Health Information Log) в библиотеке дисков.
        internal const byte NvmeCriticalWarningId = 0x01;
        internal const byte NvmeAvailableSpareId = 0x03;
        internal const byte NvmeAvailableSpareThresholdId = 0x04;
        internal const byte NvmePercentageUsedId = 0x05;
        internal const byte NvmeDataUnitsWrittenId = 0x07;
        internal const byte NvmePowerCyclesId = 0x0B;
        internal const byte NvmePowerOnHoursId = 0x0C;
        internal const byte NvmeUnsafeShutdownsId = 0x0D;
        internal const byte NvmeMediaErrorsId = 0x0E;

        // Атрибуты ATA SMART.
        internal const byte AtaReallocatedId = 0x05;
        internal const byte AtaPendingId = 0xC5;
        internal const byte AtaUncorrectableId = 0xC6;
        internal const byte AtaUnexpectedPowerLossId = 0xAE;
        internal const byte AtaPowerOffRetractId = 0xC0;

        /// <summary>Единица данных NVMe — 1000 блоков по 512 байт.</summary>
        private const double NvmeDataUnitBytes = 512_000d;

        public static DiskKind KindOf(WindowsDiskInfo? windows, DiskSmartData? smart)
        {
            if (windows?.BusType == 7 || smart?.IsUsb == true) return DiskKind.Usb;
            if (windows?.BusType == 17 || smart?.IsNvme == true) return DiskKind.Nvme;
            if (windows?.MediaType == 4) return DiskKind.SataSsd;
            if (windows?.MediaType == 3) return DiskKind.Hdd;
            if (smart != null) return smart.IsSsd ? DiskKind.SataSsd : DiskKind.Hdd;
            return DiskKind.Other;
        }

        public static DiskHealthReading Merge(WindowsDiskInfo? windows, DiskSmartData? smart)
        {
            if (windows == null && smart == null) throw new ArgumentException("Нужен хотя бы один источник");

            var kind = KindOf(windows, smart);
            int number = windows?.Number ?? smart!.DriveNumber;
            string model = !string.IsNullOrWhiteSpace(smart?.Model) ? smart!.Model.Trim()
                : !string.IsNullOrWhiteSpace(windows?.Model) ? windows!.Model.Trim()
                : $"Диск {number}";

            int? wear = null, temp = null, critical = null, spare = null, spareThreshold = null;
            double? writtenTb = null;
            long? hours = null, cycles = null, unsafeShutdowns = null, media = null, reallocated = null, pending = null, uncorrectable = null;

            if (smart != null)
            {
                temp = SaneTemperature(smart.Temperature);
                bool nvme = smart.IsNvme || (kind == DiskKind.Nvme && Find(smart, NvmePercentageUsedId) != null);
                if (nvme)
                {
                    wear = ToInt(Find(smart, NvmePercentageUsedId));
                    ulong? units = Find(smart, NvmeDataUnitsWrittenId);
                    if (units is > 0) writtenTb = units.Value * NvmeDataUnitBytes / 1e12;
                    hours = ToLong(Find(smart, NvmePowerOnHoursId));
                    cycles = ToLong(Find(smart, NvmePowerCyclesId));
                    unsafeShutdowns = ToLong(Find(smart, NvmeUnsafeShutdownsId));
                    media = ToLong(Find(smart, NvmeMediaErrorsId));
                    critical = ToInt(Find(smart, NvmeCriticalWarningId));
                    spare = ToInt(Find(smart, NvmeAvailableSpareId));
                    spareThreshold = ToInt(Find(smart, NvmeAvailableSpareThresholdId));
                }
                else
                {
                    // Остаток ресурса библиотека считает по атрибутам конкретного производителя
                    // (E7/E9/A9/AD/B1 и др.) — это надёжнее, чем угадывать атрибут самим.
                    if (smart.Life is >= 0 and <= 100 && kind != DiskKind.Hdd) wear = 100 - smart.Life.Value;
                    if (smart.HostWritesGib is > 0) writtenTb = smart.HostWritesGib.Value * 1.073741824d / 1000d;
                    // Счётчики секторов: значимы младшие 32 бита — в старших байтах сырого
                    // значения часть производителей хранит постороннее.
                    reallocated = Low32(Find(smart, AtaReallocatedId));
                    pending = Low32(Find(smart, AtaPendingId));
                    uncorrectable = Low32(Find(smart, AtaUncorrectableId));
                    unsafeShutdowns = Low32(Find(smart, AtaUnexpectedPowerLossId)) ?? Low32(Find(smart, AtaPowerOffRetractId));
                }
                if (hours == null && smart.PowerOnHours > 0) hours = (long)Math.Min(smart.PowerOnHours, long.MaxValue);
                if (cycles == null && smart.PowerOnCount > 0) cycles = (long)Math.Min(smart.PowerOnCount, long.MaxValue);
            }

            // Запасной путь — счётчики надёжности Windows, только там, где SMART молчит.
            // Ноль износа у них часто значит «не знаю», поэтому берётся только больше нуля.
            if (windows != null)
            {
                temp ??= SaneTemperature(windows.CounterTemperature);
                if (hours == null && windows.CounterPowerOnHours is > 0) hours = windows.CounterPowerOnHours;
                if (wear == null && windows.CounterWear is > 0 and <= 255 && kind != DiskKind.Hdd) wear = windows.CounterWear;
                if (uncorrectable == null && media == null &&
                    (windows.CounterReadErrorsUncorrected != null || windows.CounterWriteErrorsUncorrected != null))
                {
                    long total = (windows.CounterReadErrorsUncorrected ?? 0) + (windows.CounterWriteErrorsUncorrected ?? 0);
                    if (kind == DiskKind.Nvme) media = total; else uncorrectable = total;
                }
            }

            return new DiskHealthReading
            {
                Number = number,
                Model = model,
                Kind = kind,
                SizeBytes = windows?.SizeBytes ?? 0,
                WearPercent = wear,
                WrittenTb = writtenTb,
                TemperatureC = temp,
                PowerOnHours = hours,
                PowerCycles = cycles,
                UnsafeShutdowns = unsafeShutdowns,
                MediaErrors = media,
                Reallocated = reallocated,
                Pending = pending,
                Uncorrectable = uncorrectable,
                NvmeCriticalWarning = critical,
                AvailableSpare = spare,
                AvailableSpareThreshold = spareThreshold,
                WindowsStatus = windows?.Status ?? WindowsDiskStatus.Unknown,
                SmartStatus = smart?.Status ?? SmartDiskStatus.Unknown
            };
        }

        /// <summary>Сопоставить диски Windows и SMART по номеру физического диска.</summary>
        public static IReadOnlyList<DiskHealthReading> MergeAll(
            IReadOnlyList<WindowsDiskInfo> windows, IReadOnlyList<DiskSmartData> smart)
        {
            var result = new List<DiskHealthReading>();
            var smartByNumber = smart.GroupBy(s => s.DriveNumber).ToDictionary(g => g.Key, g => g.First());
            foreach (var w in windows)
                result.Add(Merge(w, smartByNumber.GetValueOrDefault(w.Number)));
            // Диск, который видит библиотека, но не видит Windows Storage (бывает с RAID-контроллерами).
            foreach (var s in smartByNumber.Values.Where(s => windows.All(w => w.Number != s.DriveNumber)))
                result.Add(Merge(null, s));
            return result.OrderBy(r => r.Number).ToList();
        }

        private static ulong? Find(DiskSmartData smart, byte id)
        {
            foreach (var a in smart.Attributes)
                if (a.Id == id) return a.Raw;
            return null;
        }

        // Температура вне 1…120 °C — мусор (нет датчика, значение в кельвинах и т. п.).
        private static int? SaneTemperature(int? t) => t is > 0 and < 120 ? t : null;

        private static int? ToInt(ulong? v) => v == null ? null : (int)Math.Min(v.Value, int.MaxValue);
        private static long? ToLong(ulong? v) => v == null ? null : (long)Math.Min(v.Value, long.MaxValue);
        private static long? Low32(ulong? v) => v == null ? null : (long)(v.Value & 0xFFFF_FFFF);
    }
}
