using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Ven4Tools.Services
{
    /// <summary>Вид устройства, от которого пришёл датчик.</summary>
    public enum SensorDevice { Cpu, GpuNvidia, GpuAmd, GpuIntel, Storage, Board, Other }

    /// <summary>Вид величины датчика.</summary>
    /// <summary>Вид величины датчика. Data — объём в мегабайтах (видеопамять).</summary>
    public enum SensorKind { Temperature, Clock, Load, Fan, Power, Data, Other }

    /// <summary>
    /// Одно показание как есть: от какого устройства, что меряет, как называется у
    /// библиотеки датчиков и сколько показывает (null — датчик есть, значения нет:
    /// так выглядят датчики процессора без драйвера).
    /// </summary>
    public sealed record RawSensor(SensorDevice Device, string DeviceName, SensorKind Kind, string Name, float? Value);

    /// <summary>Строка карточки: подпись и значение.</summary>
    public sealed record SensorLine(string Label, string Value);

    /// <summary>Карточка одного устройства: заголовок и строки с цифрами.</summary>
    public sealed record SensorCard(string Title, IReadOnlyList<SensorLine> Lines);

    /// <summary>
    /// Разбор сырых показаний в карточки «Процессор», «Видеокарта», «Диски», «Плата».
    /// Чистая функция — без библиотеки датчиков и без железа, поэтому покрыта
    /// модульными проверками на снимках с настоящих машин.
    /// </summary>
    public static class HardwareSnapshotBuilder
    {
        /// <summary>Частота процессора по оценке Windows, когда точных частот без драйвера нет.</summary>
        public sealed record CpuClockEstimate(float Mhz);

        public static IReadOnlyList<SensorCard> Build(
            IReadOnlyList<RawSensor> sensors, bool cpuDriverAvailable, CpuClockEstimate? clockEstimate = null)
        {
            var cards = new List<SensorCard>();

            foreach (var cpu in sensors.Where(s => s.Device == SensorDevice.Cpu).GroupBy(s => s.DeviceName))
                cards.Add(BuildCpu(cpu.Key, cpu.ToList(), cpuDriverAvailable, clockEstimate));

            foreach (var gpu in sensors.Where(s => s.Device is SensorDevice.GpuNvidia or SensorDevice.GpuAmd or SensorDevice.GpuIntel)
                         .GroupBy(s => s.DeviceName))
            {
                var card = BuildGpu(gpu.Key, gpu.ToList());
                // Встроенная графика без единой цифры (частое дело у Intel) — не карточка.
                if (card.Lines.Count > 0) cards.Add(card);
            }

            var disks = BuildDisks(sensors.Where(s => s.Device == SensorDevice.Storage).ToList());
            if (disks != null) cards.Add(disks);

            var board = BuildBoard(sensors.Where(s => s.Device == SensorDevice.Board).ToList());
            if (board != null) cards.Add(board);

            return cards;
        }

        private static SensorCard BuildCpu(string name, List<RawSensor> s, bool driver, CpuClockEstimate? estimate)
        {
            var lines = new List<SensorLine>();

            // Intel отдаёт «CPU Package» и «Core Max», AMD — «Core (Tctl/Tdie)» и «Package».
            float? package = Find(s, SensorKind.Temperature, "CPU Package", "Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Tctl/Tdie");
            float? coreMax = Find(s, SensorKind.Temperature, "Core Max");
            if (package != null || coreMax != null)
            {
                string text = package != null ? $"{Deg(package.Value)}" : Deg(coreMax!.Value);
                if (package != null && coreMax != null && coreMax.Value - package.Value >= 1)
                    text += " · " + string.Format(Tr("самое горячее ядро {0}"), Deg(coreMax.Value));
                lines.Add(new SensorLine(Tr("Температура"), text));
            }
            else
            {
                // Без кнопки установки драйвера (экспериментальная версия) «нужен драйвер»
                // звало бы туда, куда в программе пути нет.
                lines.Add(new SensorLine(Tr("Температура"), driver
                    ? Tr("датчик не отвечает")
                    : PawnIoDriver.FeatureEnabled ? Tr("нужен драйвер датчиков") : Tr("недоступна без драйвера ядра")));
            }

            string? clocks = CpuClocks(s);
            if (clocks != null) lines.Add(new SensorLine(Tr("Частота"), clocks));
            else if (estimate != null && estimate.Mhz > 0)
                lines.Add(new SensorLine(Tr("Частота"), string.Format(Tr("≈ {0} (оценка Windows)"), Mhz(estimate.Mhz))));

            float? load = Find(s, SensorKind.Load, "CPU Total");
            if (load != null) lines.Add(new SensorLine(Tr("Загрузка"), Pct(load.Value)));

            float? power = Find(s, SensorKind.Power, "CPU Package", "Package");
            if (power is >= 0.5f) lines.Add(new SensorLine(Tr("Потребление"), Watt(power.Value)));

            return new SensorCard(string.Format(Tr("Процессор · {0}"), name), lines);
        }

        // Частоты ядер у гибридных Intel — отдельно P- и E-ядра, у остальных — одним числом.
        private static string? CpuClocks(List<RawSensor> s)
        {
            var cores = s.Where(x => x.Kind == SensorKind.Clock && x.Value is > 0
                                     && x.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)).ToList();
            if (cores.Count == 0) return null;
            var p = cores.Where(x => x.Name.StartsWith("P-Core", StringComparison.OrdinalIgnoreCase)).ToList();
            var e = cores.Where(x => x.Name.StartsWith("E-Core", StringComparison.OrdinalIgnoreCase)).ToList();
            if (p.Count > 0 && e.Count > 0)
                return string.Format(Tr("P-ядра {0} · E-ядра {1}"), Mhz(p.Max(x => x.Value!.Value)), Mhz(e.Max(x => x.Value!.Value)));
            return Mhz(cores.Max(x => x.Value!.Value));
        }

        private static SensorCard BuildGpu(string name, List<RawSensor> s)
        {
            var lines = new List<SensorLine>();
            float? core = Find(s, SensorKind.Temperature, "GPU Core");
            float? hot = Find(s, SensorKind.Temperature, "GPU Hot Spot");
            if (core != null)
                lines.Add(new SensorLine(Tr("Температура"), hot != null && hot.Value - core.Value >= 1
                    ? string.Format(Tr("{0} · горячая точка {1}"), Deg(core.Value), Deg(hot.Value))
                    : Deg(core.Value)));

            float? coreClock = Find(s, SensorKind.Clock, "GPU Core");
            float? memClock = Find(s, SensorKind.Clock, "GPU Memory");
            if (coreClock is > 0)
                lines.Add(new SensorLine(Tr("Частота"), memClock is > 0
                    ? string.Format(Tr("ядро {0} · память {1}"), Mhz(coreClock.Value), Mhz(memClock.Value))
                    : string.Format(Tr("ядро {0}"), Mhz(coreClock.Value))));

            float? load = Find(s, SensorKind.Load, "GPU Core");
            if (load != null) lines.Add(new SensorLine(Tr("Загрузка"), Pct(load.Value)));

            var fans = s.Where(x => x.Kind == SensorKind.Fan && x.Value != null).Select(x => x.Value!.Value).ToList();
            if (fans.Count > 0)
                lines.Add(new SensorLine(Tr("Вентиляторы"), fans.All(f => f < 1)
                    ? Tr("стоят (тихий режим)")
                    : string.Join(" · ", fans.Select(Rpm))));

            float? power = Find(s, SensorKind.Power, "GPU Package", "GPU Power");
            if (power is >= 0.5f) lines.Add(new SensorLine(Tr("Потребление"), Watt(power.Value)));

            return new SensorCard(string.Format(Tr("Видеокарта · {0}"), name), lines);
        }

        private static SensorCard? BuildDisks(List<RawSensor> s)
        {
            var lines = new List<SensorLine>();
            foreach (var disk in s.GroupBy(x => x.DeviceName))
            {
                // «Warning/Critical Temperature» — пороги из паспорта диска, а не показание.
                float? t = disk.Where(x => x.Kind == SensorKind.Temperature && x.Value != null
                                           && !x.Name.Contains("Warning", StringComparison.OrdinalIgnoreCase)
                                           && !x.Name.Contains("Critical", StringComparison.OrdinalIgnoreCase))
                               .OrderBy(x => x.Name.StartsWith("Composite", StringComparison.OrdinalIgnoreCase) ? 0
                                           : x.Name.Equals("Temperature", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                               .Select(x => x.Value).FirstOrDefault();
                if (t != null) lines.Add(new SensorLine(disk.Key.Trim(), Deg(t.Value)));
            }
            return lines.Count == 0 ? null : new SensorCard(Tr("Диски"), lines);
        }

        private static SensorCard? BuildBoard(List<RawSensor> s)
        {
            // Температуры платы у контроллеров Super I/O безымянные («Temperature #3») —
            // человеку они ничего не скажут. Вентиляторы корпуса понятны и без имён.
            var fans = s.Where(x => x.Kind == SensorKind.Fan && x.Value is >= 1).Select(x => x.Value!.Value).ToList();
            if (fans.Count == 0) return null;
            return new SensorCard(string.Format(Tr("Материнская плата · {0}"), s[0].DeviceName),
                new[] { new SensorLine(Tr("Вентиляторы корпуса"), string.Join(" · ", fans.Select(Rpm))) });
        }

        internal static float? Find(List<RawSensor> s, SensorKind kind, params string[] names)
        {
            foreach (string name in names)
            {
                var hit = s.FirstOrDefault(x => x.Kind == kind && x.Value != null
                                                && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit.Value;
            }
            return null;
        }

        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
        internal static string Deg(float v) => $"{Math.Round(v):0} °C";
        internal static string Mhz(float v) => $"{Math.Round(v):0} {Tr("МГц")}";
        internal static string Pct(float v) => $"{Math.Round(v):0} %";
        internal static string Rpm(float v) => $"{Math.Round(v):0} {Tr("об/мин")}";
        internal static string Watt(float v) => v < 10 ? $"{v.ToString("0.0", Ru)} {Tr("Вт")}" : $"{Math.Round(v):0} {Tr("Вт")}";

        /// <summary>
        /// Две короткие строки для оверлея поверх игр: процессор и видеокарта. Видеокарта —
        /// дискретная (NVIDIA, AMD), если есть: встроенная в играх обычно не работает.
        /// </summary>
        public static IReadOnlyList<string> OverlayLines(IReadOnlyList<RawSensor> sensors)
        {
            var lines = new List<string>();
            var cpu = sensors.Where(x => x.Device == SensorDevice.Cpu).ToList();
            if (cpu.Count > 0)
            {
                var parts = new List<string>();
                float? t = Find(cpu, SensorKind.Temperature, "CPU Package", "Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Tctl/Tdie", "Core Max");
                if (t != null) parts.Add(Deg(t.Value));
                float? load = Find(cpu, SensorKind.Load, "CPU Total");
                if (load != null) parts.Add(Pct(load.Value));
                if (parts.Count > 0) lines.Add("CPU  " + string.Join(" · ", parts));
            }

            var gpus = sensors.Where(x => x.Device is SensorDevice.GpuNvidia or SensorDevice.GpuAmd).ToList();
            if (gpus.Count == 0) gpus = sensors.Where(x => x.Device == SensorDevice.GpuIntel).ToList();
            var gpu = gpus.GroupBy(x => x.DeviceName).Select(g => g.ToList()).FirstOrDefault();
            if (gpu != null)
            {
                var parts = new List<string>();
                float? t = Find(gpu, SensorKind.Temperature, "GPU Core");
                if (t != null) parts.Add(Deg(t.Value));
                float? load = Find(gpu, SensorKind.Load, "GPU Core");
                if (load != null) parts.Add(Pct(load.Value));
                float? clock = Find(gpu, SensorKind.Clock, "GPU Core");
                if (clock is > 0) parts.Add(Mhz(clock.Value));
                if (parts.Count > 0) lines.Add("GPU  " + string.Join(" · ", parts));
            }
            return lines;
        }

        /// <summary>Карточки текстом — для «Скопировать полный отчёт».</summary>
        public static string ToReport(IReadOnlyList<SensorCard> cards) =>
            string.Join(Environment.NewLine, cards.SelectMany(c =>
                new[] { c.Title }.Concat(c.Lines.Select(l => $"  {l.Label}: {l.Value}"))));
    }
}
