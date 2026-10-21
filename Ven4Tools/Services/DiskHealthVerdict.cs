using System;
using System.Collections.Generic;
using System.Globalization;

namespace Ven4Tools.Services
{
    /// <summary>Вердикт по диску. Порядок важен: чем дальше, тем серьёзнее (NoData — отдельно).</summary>
    public enum DiskVerdictLevel { Ok, Watch, Backup, NoData }

    /// <summary>Вердикт и короткие причины — каждая отдельной строкой (так они переводятся по одной).</summary>
    internal sealed record DiskVerdict(DiskVerdictLevel Level, IReadOnlyList<string> Reasons)
    {
        public string Label => Level switch
        {
            DiskVerdictLevel.Ok => "в норме",
            DiskVerdictLevel.Watch => "следить",
            DiskVerdictLevel.Backup => "делать резервную копию",
            _ => "нет данных"
        };

        public string Icon => Level switch
        {
            DiskVerdictLevel.Ok => "🟢",
            DiskVerdictLevel.Watch => "🟡",
            DiskVerdictLevel.Backup => "🔴",
            _ => "⚪"
        };

        /// <summary>Ключ кисти темы для строки вердикта.</summary>
        public string BrushKey => Level switch
        {
            DiskVerdictLevel.Ok => "StatusSuccess",
            DiskVerdictLevel.Watch => "StatusWarning",
            DiskVerdictLevel.Backup => "StatusDanger",
            _ => "TextSecondary"
        };
    }

    /// <summary>
    /// Пороги вердикта «в норме / следить / делать резервную копию». Чистый класс без
    /// железа — покрыт модульными проверками. Правило честности: если диск не отдал ни
    /// одного показателя здоровья (износ, ошибки, резерв) — «нет данных», а не «в норме».
    /// </summary>
    internal static class DiskHealthVerdict
    {
        /// <summary>Износ SSD, с которого стоит следить, %.</summary>
        public const int WearWatchPercent = 70;
        /// <summary>Износ SSD, с которого пора делать резервную копию, %.</summary>
        public const int WearBackupPercent = 90;
        /// <summary>Переназначенных секторов: 1 и больше — следить, столько и больше — копия.</summary>
        public const long ReallocatedBackup = 100;
        /// <summary>Температура HDD, при которой стоит следить, °C.</summary>
        public const int HddHotC = 55;
        /// <summary>Температура SSD/NVMe, при которой стоит следить, °C.</summary>
        public const int SsdHotC = 70;

        // Биты NVMe Critical Warning.
        private const int NvmeSpareLow = 0x01;
        private const int NvmeTemperature = 0x02;
        private const int NvmeReliabilityDegraded = 0x04;
        private const int NvmeReadOnly = 0x08;
        private const int NvmeVolatileBackupFailed = 0x10;

        public static DiskVerdict Evaluate(DiskHealthReading d)
        {
            var level = DiskVerdictLevel.Ok;
            var reasons = new List<string>();
            void Flag(DiskVerdictLevel l, string reason)
            {
                if (l > level) level = l;
                reasons.Add(reason);
            }
            static string N(long v) => v.ToString(CultureInfo.CurrentCulture);

            // ── Повод для резервной копии ────────────────────────────────────────
            if (d.MediaErrors is > 0) Flag(DiskVerdictLevel.Backup, $"Ошибки носителя: {N(d.MediaErrors.Value)}");
            if (d.Pending is > 0) Flag(DiskVerdictLevel.Backup, $"Нестабильные секторы (ожидают переназначения): {N(d.Pending.Value)}");
            if (d.Uncorrectable is > 0) Flag(DiskVerdictLevel.Backup, $"Неисправимые ошибки: {N(d.Uncorrectable.Value)}");
            if (d.Reallocated is >= ReallocatedBackup) Flag(DiskVerdictLevel.Backup, $"Переназначенные секторы: {N(d.Reallocated.Value)}");
            else if (d.Reallocated is > 0) Flag(DiskVerdictLevel.Watch, $"Переназначенные секторы: {N(d.Reallocated.Value)}");

            if (d.WearPercent is >= WearBackupPercent) Flag(DiskVerdictLevel.Backup, $"Износ {d.WearPercent}% — ресурс записи почти исчерпан");
            else if (d.WearPercent is >= WearWatchPercent) Flag(DiskVerdictLevel.Watch, $"Износ {d.WearPercent}% — ресурс записи заметно израсходован");

            if (d.AvailableSpare != null && d.AvailableSpareThreshold is > 0 && d.AvailableSpare <= d.AvailableSpareThreshold)
                Flag(DiskVerdictLevel.Backup, $"Резервная область почти исчерпана: {d.AvailableSpare}%");

            if (d.NvmeCriticalWarning is int cw && cw != 0)
            {
                if ((cw & (NvmeSpareLow | NvmeReliabilityDegraded | NvmeReadOnly | NvmeVolatileBackupFailed)) != 0)
                    Flag(DiskVerdictLevel.Backup, "Диск сам сообщает о критической неисправности");
                if ((cw & NvmeTemperature) != 0)
                    Flag(DiskVerdictLevel.Watch, "Диск сообщает о перегреве");
            }

            if (d.WindowsStatus == WindowsDiskStatus.Unhealthy) Flag(DiskVerdictLevel.Backup, "Windows отмечает диск как неисправный");
            else if (d.WindowsStatus == WindowsDiskStatus.Warning) Flag(DiskVerdictLevel.Watch, "Windows предупреждает о состоянии диска");

            // ── Повод следить ────────────────────────────────────────────────────
            int hot = d.Kind == DiskKind.Hdd ? HddHotC : SsdHotC;
            if (d.TemperatureC is int t && t >= hot) Flag(DiskVerdictLevel.Watch, $"Температура {t} °C — выше обычного");

            // Итоговая оценка SMART библиотеки (по правилам CrystalDiskInfo): поднимает
            // уровень, но причину добавляет, только если своих причин не нашлось.
            if (d.SmartStatus == SmartDiskStatus.Bad)
            {
                if (level < DiskVerdictLevel.Backup) level = DiskVerdictLevel.Backup;
                if (reasons.Count == 0) reasons.Add("Оценка SMART: плохо");
            }
            else if (d.SmartStatus == SmartDiskStatus.Caution)
            {
                if (level < DiskVerdictLevel.Watch) level = DiskVerdictLevel.Watch;
                if (reasons.Count == 0) reasons.Add("Оценка SMART: требует внимания");
            }

            if (reasons.Count > 0) return new DiskVerdict(level, reasons);

            // ── Ничего тревожного: «в норме» — только если было по чему судить ───
            if (!d.HasHealthData)
            {
                return new DiskVerdict(DiskVerdictLevel.NoData, new[]
                {
                    d.Kind == DiskKind.Usb
                        ? "Диск подключён по USB: переходник не передаёт данные SMART"
                        : "Диск не отдаёт данные SMART — оценить состояние нельзя"
                });
            }

            var ok = new List<string>();
            if (d.WearPercent != null) ok.Add($"Износ {d.WearPercent}%");
            if (d.MediaErrors != null || d.Reallocated != null || d.Pending != null || d.Uncorrectable != null)
                ok.Add("Ошибок не найдено");
            if (ok.Count == 0) ok.Add("Тревожных показателей нет");
            return new DiskVerdict(DiskVerdictLevel.Ok, ok);
        }
    }

    /// <summary>Подписи и значения для карточки диска и текстового отчёта. Чистые функции.</summary>
    internal static class DiskHealthFormat
    {
        public const string NoData = "нет данных";

        public static string KindText(DiskKind kind) => kind switch
        {
            DiskKind.Nvme => "NVMe",
            DiskKind.SataSsd => "SATA SSD",
            DiskKind.Hdd => "HDD",
            DiskKind.Usb => "USB",
            _ => "диск"
        };

        public static string Title(DiskHealthReading d)
        {
            string kind = KindText(d.Kind);
            if (d.SizeBytes == 0) return $"{d.Model} · {kind}";
            double gb = d.SizeBytes / 1e9;
            string size = gb >= 2000
                ? (gb / 1000).ToString("0.#", CultureInfo.CurrentCulture) + " ТБ"
                : gb.ToString("0", CultureInfo.CurrentCulture) + " ГБ";
            return $"{d.Model} · {kind} · {size}";
        }

        public static string Wear(DiskHealthReading d) =>
            d.WearPercent != null ? $"{d.WearPercent}%"
            : d.Kind == DiskKind.Hdd ? "у HDD не считается"
            : NoData;

        public static string Written(DiskHealthReading d)
        {
            if (d.WrittenTb == null) return NoData;
            double tb = d.WrittenTb.Value;
            return tb < 1
                ? (tb * 1000).ToString("0", CultureInfo.CurrentCulture) + " ГБ"
                : tb.ToString(tb < 100 ? "0.0" : "0", CultureInfo.CurrentCulture) + " ТБ";
        }

        public static string Temperature(DiskHealthReading d) => d.TemperatureC != null ? $"{d.TemperatureC} °C" : NoData;

        public static string Hours(DiskHealthReading d) =>
            d.PowerOnHours != null ? $"{d.PowerOnHours.Value.ToString("N0", CultureInfo.CurrentCulture)} ч" : NoData;

        public static string Count(long? v) => v != null ? v.Value.ToString("N0", CultureInfo.CurrentCulture) : NoData;

        public static string Errors(DiskHealthReading d)
        {
            if (d.Kind == DiskKind.Nvme || (d.MediaErrors != null && d.Reallocated == null))
                return d.MediaErrors != null ? $"ошибок носителя: {Count(d.MediaErrors)}" : NoData;
            if (d.Reallocated == null && d.Pending == null && d.Uncorrectable == null) return NoData;
            return $"переназначено: {Count(d.Reallocated)} · нестабильных: {Count(d.Pending)} · неисправимых: {Count(d.Uncorrectable)}";
        }

        /// <summary>Строки карточки «подпись — значение» в порядке показа.</summary>
        public static IReadOnlyList<(string Label, string Value)> Lines(DiskHealthReading d) => new[]
        {
            ("Износ", Wear(d)),
            ("Записано", Written(d)),
            ("Температура", Temperature(d)),
            ("Наработка", Hours(d)),
            ("Включений", Count(d.PowerCycles)),
            ("Ошибки", Errors(d)),
            ("Аварийные отключения", Count(d.UnsafeShutdowns)),
        };
    }
}
