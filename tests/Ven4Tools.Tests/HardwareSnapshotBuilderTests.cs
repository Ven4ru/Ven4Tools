using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ven4Tools.Services;
using Xunit;

namespace Ven4Tools.Tests
{
    /// <summary>
    /// Разбор показаний датчиков в карточки. Снимки — сокращённые настоящие показания
    /// LibreHardwareMonitorLib 0.9.6 с ПК на Intel Core i5-14500T, RTX 2070 SUPER и
    /// плате Gigabyte B660 (15.10.2026), без драйвера и с драйвером PawnIO.
    /// </summary>
    public class HardwareSnapshotBuilderTests
    {
        private const string Cpu = "Intel Core i5-14500T";
        private const string Gpu = "NVIDIA GeForce RTX 2070 SUPER";

        private static List<RawSensor> CpuWithoutDriver() => new()
        {
            new(SensorDevice.Cpu, Cpu, SensorKind.Load, "CPU Total", 5.7f),
            new(SensorDevice.Cpu, Cpu, SensorKind.Temperature, "Core Max", null),
            new(SensorDevice.Cpu, Cpu, SensorKind.Temperature, "CPU Package", null),
            new(SensorDevice.Cpu, Cpu, SensorKind.Clock, "P-Core #1", null),
            new(SensorDevice.Cpu, Cpu, SensorKind.Power, "CPU Package", 0f),
        };

        private static List<RawSensor> CpuWithDriver() => new()
        {
            new(SensorDevice.Cpu, Cpu, SensorKind.Load, "CPU Total", 5.7f),
            new(SensorDevice.Cpu, Cpu, SensorKind.Temperature, "Core Max", 26f),
            new(SensorDevice.Cpu, Cpu, SensorKind.Temperature, "CPU Package", 26f),
            new(SensorDevice.Cpu, Cpu, SensorKind.Clock, "P-Core #1", 4075.1f),
            new(SensorDevice.Cpu, Cpu, SensorKind.Clock, "P-Core #2", 4075.1f),
            new(SensorDevice.Cpu, Cpu, SensorKind.Clock, "E-Core #1", 3379.4f),
            new(SensorDevice.Cpu, Cpu, SensorKind.Clock, "Bus Speed", 99.4f),
            new(SensorDevice.Cpu, Cpu, SensorKind.Power, "CPU Package", 21f),
        };

        private static List<RawSensor> Nvidia() => new()
        {
            new(SensorDevice.GpuNvidia, Gpu, SensorKind.Temperature, "GPU Core", 28f),
            new(SensorDevice.GpuNvidia, Gpu, SensorKind.Temperature, "GPU Hot Spot", 38.8f),
            new(SensorDevice.GpuNvidia, Gpu, SensorKind.Clock, "GPU Core", 300f),
            new(SensorDevice.GpuNvidia, Gpu, SensorKind.Clock, "GPU Memory", 405f),
            new(SensorDevice.GpuNvidia, Gpu, SensorKind.Fan, "GPU Fan 1", 850f),
            new(SensorDevice.GpuNvidia, Gpu, SensorKind.Fan, "GPU Fan 2", 834f),
            new(SensorDevice.GpuNvidia, Gpu, SensorKind.Load, "GPU Core", 8f),
            new(SensorDevice.GpuNvidia, Gpu, SensorKind.Power, "GPU Package", 13.4f),
        };

        private static string Value(SensorCard card, string label) =>
            card.Lines.Single(l => l.Label == label).Value;

        [Fact]
        public void Процессор_БезДрайвера_ПросимДрайверИПоказываемОценкуЧастоты()
        {
            var cards = HardwareSnapshotBuilder.Build(CpuWithoutDriver(), cpuDriverAvailable: false,
                new HardwareSnapshotBuilder.CpuClockEstimate(2960f));
            var cpu = cards.Single();

            Assert.Equal("Процессор · " + Cpu, cpu.Title);
            Assert.Equal("нужен драйвер датчиков", Value(cpu, "Температура"));
            Assert.Equal("≈ 2960 МГц (оценка Windows)", Value(cpu, "Частота"));
            Assert.Equal("6 %", Value(cpu, "Загрузка"));
            // Нулевое потребление без драйвера — не показание, строки нет.
            Assert.DoesNotContain(cpu.Lines, l => l.Label == "Потребление");
        }

        [Fact]
        public void Процессор_СДрайвером_ТемператураЧастотыЯдерИПотребление()
        {
            var cpu = HardwareSnapshotBuilder.Build(CpuWithDriver(), cpuDriverAvailable: true).Single();

            Assert.Equal("26 °C", Value(cpu, "Температура"));
            // Частота шины — не частота ядра.
            Assert.Equal("P-ядра 4075 МГц · E-ядра 3379 МГц", Value(cpu, "Частота"));
            Assert.Equal("21 Вт", Value(cpu, "Потребление"));
        }

        [Theory]
        [InlineData(26f, 31f, "26 °C · самое горячее ядро 31 °C")]
        [InlineData(28f, 27f, "28 °C")] // ядро холоднее пакета — подпись про ядро не нужна
        public void Процессор_СамоеГорячееЯдро_ТолькоЕслиГорячееПакета(float package, float coreMax, string expected)
        {
            var sensors = new List<RawSensor>
            {
                new(SensorDevice.Cpu, Cpu, SensorKind.Temperature, "CPU Package", package),
                new(SensorDevice.Cpu, Cpu, SensorKind.Temperature, "Core Max", coreMax),
            };
            var cpu = HardwareSnapshotBuilder.Build(sensors, cpuDriverAvailable: true).Single();
            Assert.Equal(expected, Value(cpu, "Температура"));
        }

        [Fact]
        public void Процессор_ДрайверЕстьНоТемпературыНет_НеПросимДрайверЕщёРаз()
        {
            var cpu = HardwareSnapshotBuilder.Build(CpuWithoutDriver(), cpuDriverAvailable: true).Single();
            Assert.Equal("датчик не отвечает", Value(cpu, "Температура"));
        }

        [Fact]
        public void ПроцессорAmd_ТемператураПоTctl()
        {
            var sensors = new List<RawSensor>
            {
                new(SensorDevice.Cpu, "AMD Ryzen 7 7800X3D", SensorKind.Temperature, "Core (Tctl/Tdie)", 71.4f),
                new(SensorDevice.Cpu, "AMD Ryzen 7 7800X3D", SensorKind.Clock, "Core #1", 4850f),
                new(SensorDevice.Cpu, "AMD Ryzen 7 7800X3D", SensorKind.Clock, "Core #2", 4790f),
            };
            var cpu = HardwareSnapshotBuilder.Build(sensors, cpuDriverAvailable: true).Single();

            Assert.Equal("71 °C", Value(cpu, "Температура"));
            Assert.Equal("4850 МГц", Value(cpu, "Частота"));
        }

        [Fact]
        public void Видеокарта_ВсеСтроки()
        {
            var gpu = HardwareSnapshotBuilder.Build(Nvidia(), cpuDriverAvailable: false).Single();

            Assert.Equal("Видеокарта · " + Gpu, gpu.Title);
            Assert.Equal("28 °C · горячая точка 39 °C", Value(gpu, "Температура"));
            Assert.Equal("ядро 300 МГц · память 405 МГц", Value(gpu, "Частота"));
            Assert.Equal("8 %", Value(gpu, "Загрузка"));
            Assert.Equal("850 об/мин · 834 об/мин", Value(gpu, "Вентиляторы"));
            Assert.Equal("13 Вт", Value(gpu, "Потребление"));
        }

        [Fact]
        public void Видеокарта_ВентиляторыСтоят_ТихийРежим()
        {
            var sensors = new List<RawSensor>
            {
                new(SensorDevice.GpuNvidia, Gpu, SensorKind.Temperature, "GPU Core", 40f),
                new(SensorDevice.GpuNvidia, Gpu, SensorKind.Fan, "GPU Fan 1", 0f),
            };
            var gpu = HardwareSnapshotBuilder.Build(sensors, false).Single();
            Assert.Equal("стоят (тихий режим)", Value(gpu, "Вентиляторы"));
        }

        [Fact]
        public void ВстроеннаяГрафикаБезЦифр_НеКарточка()
        {
            var sensors = new List<RawSensor>
            {
                new(SensorDevice.GpuIntel, "Intel(R) UHD Graphics 770", SensorKind.Power, "GPU Power", 0f),
            };
            Assert.Empty(HardwareSnapshotBuilder.Build(sensors, false));
        }

        [Fact]
        public void Диски_БеремТекущуюТемпературу_АНеПорогиИзПаспорта()
        {
            var sensors = new List<RawSensor>
            {
                new(SensorDevice.Storage, "BC511 NVMe SK hynix 256GB", SensorKind.Temperature, "Warning Temperature", 82f),
                new(SensorDevice.Storage, "BC511 NVMe SK hynix 256GB", SensorKind.Temperature, "Temperature #2", 41.9f),
                new(SensorDevice.Storage, "BC511 NVMe SK hynix 256GB", SensorKind.Temperature, "Composite Temperature", 32f),
                new(SensorDevice.Storage, "BC511 NVMe SK hynix 256GB", SensorKind.Temperature, "Critical Temperature", 84f),
                new(SensorDevice.Storage, "MTFDDAK960TDS ", SensorKind.Temperature, "Temperature", 29f),
                new(SensorDevice.Storage, "MTFDDAK960TDS ", SensorKind.Load, "Total Activity", 100f),
            };
            var disks = HardwareSnapshotBuilder.Build(sensors, false).Single();

            Assert.Equal("Диски", disks.Title);
            Assert.Equal("32 °C", Value(disks, "BC511 NVMe SK hynix 256GB"));
            Assert.Equal("29 °C", Value(disks, "MTFDDAK960TDS"));
        }

        [Fact]
        public void Плата_ТолькоВращающиесяВентиляторы()
        {
            var sensors = new List<RawSensor>
            {
                new(SensorDevice.Board, "ITE IT8689E", SensorKind.Temperature, "Temperature #1", 27f),
                new(SensorDevice.Board, "ITE IT8689E", SensorKind.Fan, "Fan #1", 0f),
                new(SensorDevice.Board, "ITE IT8689E", SensorKind.Fan, "Fan #2", 693f),
                new(SensorDevice.Board, "ITE IT8689E", SensorKind.Fan, "Fan #4", 907.3f),
            };
            var board = HardwareSnapshotBuilder.Build(sensors, true).Single();

            Assert.Equal("Материнская плата · ITE IT8689E", board.Title);
            Assert.Equal("693 об/мин · 907 об/мин", Value(board, "Вентиляторы корпуса"));
        }

        [Fact]
        public void ПлатаБезДрайвера_НеКарточка()
        {
            Assert.Empty(HardwareSnapshotBuilder.Build(new List<RawSensor>(), false));
        }

        [Fact]
        public void Отчёт_КарточкиТекстом()
        {
            var cards = HardwareSnapshotBuilder.Build(CpuWithDriver().Concat(Nvidia()).ToList(), true);
            string report = HardwareSnapshotBuilder.ToReport(cards);

            Assert.Contains("Процессор · " + Cpu, report);
            Assert.Contains("  Температура: 26 °C", report);
            Assert.Contains("Видеокарта · " + Gpu, report);
        }

        [Fact]
        public void PawnIo_ОпубликованныйInfИщетсяПоСодержимому()
        {
            string dir = Path.Combine(Path.GetTempPath(), "v4t-inf-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "oem2.inf"), "[SourceDisksFiles]\r\nPawnIO.sys = 1\r\n");
                File.WriteAllText(Path.Combine(dir, "oem3.inf"), "[SourceDisksFiles]\r\nnvlddmkm.sys = 1\r\n");
                File.WriteAllText(Path.Combine(dir, "pawnio.inf"), "PawnIO.sys");

                Assert.Equal(new[] { "oem2.inf" }, PawnIoDriver.FindPublishedInfs(dir));
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
