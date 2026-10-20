using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace Ven4Tools.Services
{
    /// <summary>Сырые показания батареи: заводская и текущая полная ёмкость (мВт·ч), число циклов.</summary>
    internal sealed record BatteryRaw(long? DesignedMwh, long? FullChargedMwh, long? Cycles);

    /// <summary>Итог для показа: есть ли достоверные данные, процент от заводской ёмкости и готовая строка.</summary>
    internal sealed record BatteryHealthResult(bool HasData, int? Percent, long? DesignedMwh, long? FullChargedMwh, long? Cycles, string Text);

    /// <summary>
    /// Расчёт износа батареи. Чистые функции — покрыты модульными проверками.
    /// Мусорные значения (ноль, ёмкость больше заводской в разы) — «нет данных»,
    /// а не выдуманный процент.
    /// </summary>
    internal static class BatteryHealthCalculator
    {
        /// <summary>Текущая ёмкость больше заводской во столько раз — уже мусор (новые батареи дают до ~110 %).</summary>
        public const double MaxPlausibleRatio = 1.3;
        /// <summary>Заводская ёмкость больше этого — мусор: у ноутбуков она в пределах 20–150 Вт·ч.</summary>
        public const long MaxPlausibleDesignedMwh = 500_000;
        /// <summary>Циклов больше этого — мусор.</summary>
        public const long MaxPlausibleCycles = 20_000;

        public static BatteryHealthResult Evaluate(BatteryRaw raw)
        {
            long? cycles = raw.Cycles is > 0 and <= MaxPlausibleCycles ? raw.Cycles : null;
            string cyclesText = cycles != null ? cycles.Value.ToString("N0", CultureInfo.CurrentCulture) : "нет данных";

            if (raw.DesignedMwh is not > 0 || raw.FullChargedMwh is not > 0 ||
                raw.DesignedMwh > MaxPlausibleDesignedMwh ||
                raw.FullChargedMwh > raw.DesignedMwh * MaxPlausibleRatio)
            {
                return new BatteryHealthResult(false, null, null, null, cycles,
                    $"Ёмкость: нет данных (батарея не сообщает достоверную ёмкость), циклов: {cyclesText}");
            }

            long designed = raw.DesignedMwh.Value;
            long full = raw.FullChargedMwh.Value;
            int percent = (int)Math.Round(full * 100d / designed, MidpointRounding.AwayFromZero);
            string Mwh(long v) => v.ToString("N0", CultureInfo.CurrentCulture);
            return new BatteryHealthResult(true, percent, designed, full, cycles,
                $"Ёмкость: {percent}% от заводской ({Mwh(full)} из {Mwh(designed)} мВт·ч), циклов: {cyclesText}");
        }

        /// <summary>Несколько батарей (бывает у ноутбуков с двумя) — ёмкости складываются, циклы — наибольшие.</summary>
        public static BatteryRaw Combine(IReadOnlyList<BatteryRaw> batteries)
        {
            if (batteries.Count == 0) return new BatteryRaw(null, null, null);
            if (batteries.Count == 1) return batteries[0];
            bool allDesigned = batteries.All(b => b.DesignedMwh is > 0);
            bool allFull = batteries.All(b => b.FullChargedMwh is > 0);
            return new BatteryRaw(
                allDesigned ? batteries.Sum(b => b.DesignedMwh!.Value) : null,
                allFull ? batteries.Sum(b => b.FullChargedMwh!.Value) : null,
                batteries.Max(b => b.Cycles));
        }

        /// <summary>
        /// Разбор XML-отчёта <c>powercfg /batteryreport /xml</c> (запасной путь, когда
        /// WMI root\wmi молчит). Пространство имён отчёта меняется между версиями
        /// Windows, поэтому элементы ищутся по локальному имени.
        /// </summary>
        public static IReadOnlyList<BatteryRaw> ParsePowercfgXml(string xml)
        {
            var result = new List<BatteryRaw>();
            if (string.IsNullOrWhiteSpace(xml)) return result;
            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch (System.Xml.XmlException) { return result; }

            foreach (var battery in doc.Descendants().Where(e => e.Name.LocalName == "Battery"))
            {
                long? Value(string name)
                {
                    var el = battery.Elements().FirstOrDefault(e => e.Name.LocalName == name);
                    return el != null && long.TryParse(el.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;
                }
                result.Add(new BatteryRaw(Value("DesignCapacity"), Value("FullChargeCapacity"), Value("CycleCount")));
            }
            return result;
        }
    }
}
