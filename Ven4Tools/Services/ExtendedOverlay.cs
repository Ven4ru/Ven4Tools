using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Ven4Tools.Services
{
    /// <summary>Вид строки расширенного оверлея: заголовок раздела, обычная, приглушённая, предупреждение.</summary>
    public enum OverlayLineStyle { Header, Normal, Dim, Warning }

    public sealed record OverlayLine(string Text, OverlayLineStyle Style = OverlayLineStyle.Normal);

    /// <summary>Настроение Венчика в оверлее по температурам.</summary>
    public enum ThermalMood { Cold, Ok, Hot }

    /// <summary>Кадры окна на переднем плане: FPS за секунду, 1 % low и время кадра за последние секунды.</summary>
    public sealed record FrameStats(double? Fps, double? OnePercentLow, double? FrameMs);

    /// <summary>Оперативная память: занято и всего, ГБ.</summary>
    public sealed record RamInfo(double UsedGb, double TotalGb);

    /// <summary>
    /// Максимумы с момента включения оверлея — для первого запуска сборки: поиграл
    /// полчаса и видно, до скольки нагрелось и сколько потребляло.
    /// </summary>
    public sealed class OverlaySessionMax
    {
        public DateTime Started { get; } = DateTime.Now;
        public float? CpuTemp, GpuTemp, CpuPower, GpuPower, CpuClock, GpuClock;

        public void Update(IReadOnlyList<RawSensor> sensors)
        {
            var cpu = sensors.Where(x => x.Device == SensorDevice.Cpu).ToList();
            var gpu = ExtendedOverlay.MainGpu(sensors);
            Max(ref CpuTemp, ExtendedOverlay.CpuTemp(cpu));
            Max(ref CpuPower, HardwareSnapshotBuilder.Find(cpu, SensorKind.Power, "CPU Package", "Package"));
            Max(ref CpuClock, cpu.Where(x => x.Kind == SensorKind.Clock && x.Value is > 0
                                             && x.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                                 .Select(x => x.Value).DefaultIfEmpty(null).Max());
            Max(ref GpuTemp, HardwareSnapshotBuilder.Find(gpu, SensorKind.Temperature, "GPU Core"));
            Max(ref GpuPower, HardwareSnapshotBuilder.Find(gpu, SensorKind.Power, "GPU Package", "GPU Power"));
            Max(ref GpuClock, HardwareSnapshotBuilder.Find(gpu, SensorKind.Clock, "GPU Core"));
        }

        private static void Max(ref float? current, float? value)
        {
            if (value is > 0 && (current == null || value > current)) current = value;
        }
    }

    /// <summary>
    /// Строки расширенного оверлея. Чистая функция — без окна, библиотеки датчиков и
    /// PresentMon, поэтому покрыта модульными проверками.
    /// </summary>
    public static class ExtendedOverlay
    {
        // Ближе 5 °C к TjMax процессор Intel начинает сбрасывать частоту.
        private const float ThrottleMarginC = 5f;
        // У AMD запаса до предела библиотека не даёт; Tctl от 95 °C — уже у предела.
        private const float AmdThrottleC = 95f;

        public static IReadOnlyList<OverlayLine> Build(
            IReadOnlyList<RawSensor> sensors, FrameStats frames, RamInfo? ram, OverlaySessionMax session, DateTime now)
        {
            var lines = new List<OverlayLine>();
            var cpu = sensors.Where(x => x.Device == SensorDevice.Cpu).ToList();
            var gpu = MainGpu(sensors);

            // Кадры
            lines.Add(new OverlayLine(frames.Fps != null
                ? string.Join(" · ", new[]
                  {
                      $"FPS {Math.Round(frames.Fps.Value):0}",
                      frames.OnePercentLow != null ? $"1% low {Math.Round(frames.OnePercentLow.Value):0}" : null,
                      frames.FrameMs != null ? $"{frames.FrameMs.Value.ToString("0.0", CultureInfo.InvariantCulture)} {Tr("мс")}" : null
                  }.Where(p => p != null))
                : "FPS —", OverlayLineStyle.Header));

            // Процессор
            if (cpu.Count > 0)
            {
                lines.Add(new OverlayLine(cpu[0].DeviceName, OverlayLineStyle.Header));
                var parts = new List<string>();
                float? t = CpuTemp(cpu);
                if (t != null) parts.Add(HardwareSnapshotBuilder.Deg(t.Value));
                float? load = HardwareSnapshotBuilder.Find(cpu, SensorKind.Load, "CPU Total");
                if (load != null) parts.Add(HardwareSnapshotBuilder.Pct(load.Value));
                float? power = HardwareSnapshotBuilder.Find(cpu, SensorKind.Power, "CPU Package", "Package");
                if (power is >= 0.5f) parts.Add(HardwareSnapshotBuilder.Watt(power.Value));
                if (parts.Count > 0) lines.Add(new OverlayLine(string.Join(" · ", parts)));

                lines.AddRange(CoreLines(cpu).Select(l => new OverlayLine(l, OverlayLineStyle.Dim)));

                if (IsNearThermalLimit(cpu))
                    lines.Add(new OverlayLine(Tr("⚠ процессор у предела температуры — частота снижается"), OverlayLineStyle.Warning));
            }

            // Видеокарта
            if (gpu.Count > 0)
            {
                lines.Add(new OverlayLine(gpu[0].DeviceName, OverlayLineStyle.Header));
                var parts = new List<string>();
                float? t = HardwareSnapshotBuilder.Find(gpu, SensorKind.Temperature, "GPU Core");
                float? hot = HardwareSnapshotBuilder.Find(gpu, SensorKind.Temperature, "GPU Hot Spot");
                if (t != null) parts.Add(hot != null && hot - t >= 1
                    ? $"{HardwareSnapshotBuilder.Deg(t.Value)} ({Tr("горячая точка")} {HardwareSnapshotBuilder.Deg(hot.Value)})"
                    : HardwareSnapshotBuilder.Deg(t.Value));
                float? load = HardwareSnapshotBuilder.Find(gpu, SensorKind.Load, "GPU Core");
                if (load != null) parts.Add(HardwareSnapshotBuilder.Pct(load.Value));
                float? clock = HardwareSnapshotBuilder.Find(gpu, SensorKind.Clock, "GPU Core");
                if (clock is > 0) parts.Add(HardwareSnapshotBuilder.Mhz(clock.Value));
                float? power = HardwareSnapshotBuilder.Find(gpu, SensorKind.Power, "GPU Package", "GPU Power");
                if (power is >= 0.5f) parts.Add(HardwareSnapshotBuilder.Watt(power.Value));
                if (parts.Count > 0) lines.Add(new OverlayLine(string.Join(" · ", parts)));

                float? used = HardwareSnapshotBuilder.Find(gpu, SensorKind.Data, "GPU Memory Used", "D3D Dedicated Memory Used");
                float? total = HardwareSnapshotBuilder.Find(gpu, SensorKind.Data, "GPU Memory Total");
                if (used != null && total is > 0)
                    lines.Add(new OverlayLine(string.Format(Tr("Видеопамять {0} из {1}"), Gb(used.Value / 1024), Gb(total.Value / 1024))));

                var fans = gpu.Where(x => x.Kind == SensorKind.Fan && x.Value != null).Select(x => x.Value!.Value).ToList();
                if (fans.Count > 0)
                    lines.Add(new OverlayLine(Tr("Вентиляторы") + " " + (fans.All(f => f < 1)
                        ? Tr("стоят (тихий режим)")
                        : string.Join(" · ", fans.Select(HardwareSnapshotBuilder.Rpm))), OverlayLineStyle.Dim));
            }

            // Память, корпус, диски
            if (ram != null && ram.TotalGb > 0)
                lines.Add(new OverlayLine(string.Format(Tr("Оперативная память {0} из {1}"), Gb(ram.UsedGb), Gb(ram.TotalGb))));

            var caseFans = sensors.Where(x => x.Device == SensorDevice.Board && x.Kind == SensorKind.Fan && x.Value is >= 1)
                                  .Select(x => x.Value!.Value).ToList();
            if (caseFans.Count > 0)
                lines.Add(new OverlayLine(Tr("Корпус") + " " + string.Join(" · ", caseFans.Select(HardwareSnapshotBuilder.Rpm)), OverlayLineStyle.Dim));

            var disks = sensors.Where(x => x.Device == SensorDevice.Storage).GroupBy(x => x.DeviceName)
                .Select(d => d.Where(x => x.Kind == SensorKind.Temperature && x.Value != null
                                          && !x.Name.Contains("Warning", StringComparison.OrdinalIgnoreCase)
                                          && !x.Name.Contains("Critical", StringComparison.OrdinalIgnoreCase))
                              .Select(x => x.Value).FirstOrDefault())
                .Where(v => v != null).Select(v => HardwareSnapshotBuilder.Deg(v!.Value)).ToList();
            if (disks.Count > 0)
                lines.Add(new OverlayLine(Tr("Диски") + " " + string.Join(" · ", disks), OverlayLineStyle.Dim));

            // Максимумы за сеанс
            var maxTemp = new List<string>();
            if (session.CpuTemp != null) maxTemp.Add($"CPU {HardwareSnapshotBuilder.Deg(session.CpuTemp.Value)}");
            if (session.GpuTemp != null) maxTemp.Add($"GPU {HardwareSnapshotBuilder.Deg(session.GpuTemp.Value)}");
            var maxPower = new List<string>();
            if (session.CpuPower != null) maxPower.Add($"CPU {HardwareSnapshotBuilder.Watt(session.CpuPower.Value)}");
            if (session.GpuPower != null) maxPower.Add($"GPU {HardwareSnapshotBuilder.Watt(session.GpuPower.Value)}");
            if (maxTemp.Count + maxPower.Count > 0)
            {
                lines.Add(new OverlayLine(Tr("Максимум за сеанс"), OverlayLineStyle.Header));
                if (maxTemp.Count > 0) lines.Add(new OverlayLine(string.Join(" · ", maxTemp)));
                if (maxPower.Count > 0) lines.Add(new OverlayLine(string.Join(" · ", maxPower)));
            }

            var elapsed = now - session.Started;
            lines.Add(new OverlayLine($"{now:HH:mm} · {Tr("сеанс")} {(int)elapsed.TotalHours}:{elapsed.Minutes:00}", OverlayLineStyle.Dim));
            return lines;
        }

        /// <summary>
        /// Ядра по два в строке: «P1 54° 4875  P2 55° 4875». Температуры есть только с
        /// драйвером датчиков; без него ядра не показываются вовсе.
        /// </summary>
        internal static List<string> CoreLines(List<RawSensor> cpu)
        {
            var cores = new List<(string Name, float? Temp, float? Clock)>();
            foreach (var clock in cpu.Where(x => x.Kind == SensorKind.Clock && x.Value is > 0
                                                 && x.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)))
            {
                float? temp = cpu.FirstOrDefault(x => x.Kind == SensorKind.Temperature && x.Name == clock.Name)?.Value;
                cores.Add((Short(clock.Name), temp, clock.Value));
            }
            if (cores.Count == 0) return new List<string>();
            var cells = cores.Select(c => $"{c.Name,-4}{(c.Temp != null ? $"{Math.Round(c.Temp.Value):0}°" : "  ")} {Math.Round(c.Clock!.Value),5:0}").ToList();
            var lines = new List<string>();
            for (int i = 0; i < cells.Count; i += 2)
                lines.Add(i + 1 < cells.Count ? $"{cells[i]}   {cells[i + 1]}" : cells[i]);
            return lines;
        }

        // «P-Core #3» → «P3», «E-Core #12» → «E12», «Core #5» → «C5».
        private static string Short(string name)
        {
            string number = new string(name.Where(char.IsDigit).ToArray());
            return name.StartsWith("P-", StringComparison.OrdinalIgnoreCase) ? "P" + number
                 : name.StartsWith("E-", StringComparison.OrdinalIgnoreCase) ? "E" + number
                 : "C" + number;
        }

        // Пороги настроения Венчика. «Жарко» — у процессора от 85 °C (или у предела
        // TjMax), у видеокарты от 83 °C: дальше обычно начинается снижение частот.
        // «Холодно» — всё, что известно, ниже 40 °C: компьютер почти без нагрузки.
        private const float HotCpuC = 85f, HotGpuC = 83f, ColdC = 40f;

        /// <summary>
        /// Венчик: жарко, нормально или холодно. Учитываются только известные температуры
        /// (без драйвера датчиков — только видеокарта); нет ни одной — «нормально».
        /// </summary>
        public static ThermalMood Mood(IReadOnlyList<RawSensor> sensors)
        {
            var cpu = sensors.Where(x => x.Device == SensorDevice.Cpu).ToList();
            float? cpuT = CpuTemp(cpu);
            float? gpuT = HardwareSnapshotBuilder.Find(MainGpu(sensors), SensorKind.Temperature, "GPU Core");
            if (cpuT >= HotCpuC || gpuT >= HotGpuC || IsNearThermalLimit(cpu)) return ThermalMood.Hot;
            var known = new[] { cpuT, gpuT }.Where(t => t != null).ToList();
            if (known.Count > 0 && known.All(t => t < ColdC)) return ThermalMood.Cold;
            return ThermalMood.Ok;
        }

        internal static float? CpuTemp(List<RawSensor> cpu) =>
            HardwareSnapshotBuilder.Find(cpu, SensorKind.Temperature, "CPU Package", "Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Tctl/Tdie", "Core Max");

        internal static bool IsNearThermalLimit(List<RawSensor> cpu)
        {
            var margins = cpu.Where(x => x.Kind == SensorKind.Temperature && x.Value != null
                                         && x.Name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase))
                             .Select(x => x.Value!.Value).ToList();
            if (margins.Count > 0) return margins.Min() <= ThrottleMarginC;
            float? tctl = HardwareSnapshotBuilder.Find(cpu, SensorKind.Temperature, "Core (Tctl/Tdie)", "Core (Tctl)", "Tctl/Tdie");
            return tctl >= AmdThrottleC;
        }

        internal static List<RawSensor> MainGpu(IReadOnlyList<RawSensor> sensors)
        {
            var gpus = sensors.Where(x => x.Device is SensorDevice.GpuNvidia or SensorDevice.GpuAmd).ToList();
            if (gpus.Count == 0) gpus = sensors.Where(x => x.Device == SensorDevice.GpuIntel).ToList();
            return gpus.GroupBy(x => x.DeviceName).Select(g => g.ToList()).FirstOrDefault() ?? new List<RawSensor>();
        }

        // Единица — шаблоном «{0} ГБ»: отдельное «ГБ» в переводе намеренно пропущено.
        private static string Gb(double v) => string.Format(Tr("{0} ГБ"), v.ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")));
    }
}
