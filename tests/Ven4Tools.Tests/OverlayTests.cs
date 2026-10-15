using System;
using System.Collections.Generic;
using System.Linq;
using Ven4Tools.Services;
using Xunit;

namespace Ven4Tools.Tests
{
    /// <summary>Оверлей: разбор строк PresentMon, FPS и короткие строки датчиков.</summary>
    public class OverlayTests
    {
        // Настоящая строка PresentMon 2.6.0 (CSV, --output_stdout). Столбцы: ProcessID — 1,
        // MsBetweenPresents — 10, MsBetweenDisplayChange — 11.
        private const string EdgeFrame =
            "msedgewebview2.exe,26500,0x245483CAA90,DXGI,0,0,0,Composed: Flip,9.6607,NA,5.52250000000000,NA,0.04180000000000,0.17950000000000,NA,4.2200,5.4825,5.4407,0.0418,5.4955,0.1247,0.1210,0.0037,NA,NA,NA,NA,NA";

        [Fact]
        public void Кадр_БезСменыНаЭкране_БерётсяВремяМеждуPresent()
        {
            Assert.True(FpsMath.TryParseFrame(EdgeFrame, 1, 11, 10, out int pid, out double ms));
            Assert.Equal(26500, pid);
            Assert.Equal(5.5225, ms, 4);
        }

        [Fact]
        public void Кадр_СоСменойНаЭкране_БерётсяОна()
        {
            string line = "game.exe,4242,0x1,DXGI,1,0,0,Hardware: Independent Flip,1,NA,16.6,16.7,0.1";
            Assert.True(FpsMath.TryParseFrame(line, 1, 11, 10, out int pid, out double ms));
            Assert.Equal(4242, pid);
            Assert.Equal(16.7, ms, 3);
        }

        [Theory]
        [InlineData("Application,ProcessID,SwapChainAddress")]
        [InlineData("game.exe,не-число,0x1")]
        [InlineData("game.exe,10,0x1,DXGI,1,0,0,Flip,1,NA,NA,NA")]
        public void Мусор_НеКадр(string line)
        {
            Assert.False(FpsMath.TryParseFrame(line, 1, 11, 10, out _, out _));
        }

        [Fact]
        public void Fps_ПоСреднемуВремениКадра()
        {
            Assert.Equal(60.0, FpsMath.Fps(new[] { 16.0, 17.0, 17.0, 16.6667 })!.Value, 0);
            Assert.Null(FpsMath.Fps(new[] { 16.0 }));
        }

        [Fact]
        public void СтрокиОверлея_ПроцессорИДискретнаяВидеокарта()
        {
            var sensors = new List<RawSensor>
            {
                new(SensorDevice.Cpu, "Intel Core i5-14500T", SensorKind.Temperature, "CPU Package", 64.4f),
                new(SensorDevice.Cpu, "Intel Core i5-14500T", SensorKind.Load, "CPU Total", 37f),
                new(SensorDevice.GpuIntel, "Intel(R) UHD Graphics 770", SensorKind.Load, "GPU Core", 1f),
                new(SensorDevice.GpuNvidia, "NVIDIA GeForce RTX 2070 SUPER", SensorKind.Temperature, "GPU Core", 71f),
                new(SensorDevice.GpuNvidia, "NVIDIA GeForce RTX 2070 SUPER", SensorKind.Load, "GPU Core", 98f),
                new(SensorDevice.GpuNvidia, "NVIDIA GeForce RTX 2070 SUPER", SensorKind.Clock, "GPU Core", 1905f),
            };
            Assert.Equal(new[] { "CPU  64 °C · 37 %", "GPU  71 °C · 98 % · 1905 МГц" },
                HardwareSnapshotBuilder.OverlayLines(sensors));
        }

        [Fact]
        public void СтрокиОверлея_БезДрайвера_ПроцессорТолькоЗагрузка()
        {
            var sensors = new List<RawSensor>
            {
                new(SensorDevice.Cpu, "Intel Core i5-14500T", SensorKind.Temperature, "CPU Package", null),
                new(SensorDevice.Cpu, "Intel Core i5-14500T", SensorKind.Load, "CPU Total", 12f),
            };
            Assert.Equal(new[] { "CPU  12 %" }, HardwareSnapshotBuilder.OverlayLines(sensors));
        }

        [Fact]
        public void ОдинПроцентLow_ПоСамымДолгимКадрам()
        {
            var frames = Enumerable.Repeat(7.0, 99).Concat(new[] { 20.0 }).ToList();
            Assert.Equal(50.0, FpsMath.OnePercentLow(frames)!.Value, 1);
            Assert.Null(FpsMath.OnePercentLow(new[] { 7.0, 7.0 }));
        }

        private static List<RawSensor> Intel(float tjMaxMargin) => new()
        {
            new(SensorDevice.Cpu, "Intel Core i5-14500T", SensorKind.Temperature, "CPU Package", 74f),
            new(SensorDevice.Cpu, "Intel Core i5-14500T", SensorKind.Load, "CPU Total", 46f),
            new(SensorDevice.Cpu, "Intel Core i5-14500T", SensorKind.Temperature, "P-Core #1", 69f),
            new(SensorDevice.Cpu, "Intel Core i5-14500T", SensorKind.Clock, "P-Core #1", 4675f),
            new(SensorDevice.Cpu, "Intel Core i5-14500T", SensorKind.Temperature, "P-Core #1 Distance to TjMax", tjMaxMargin),
            new(SensorDevice.Cpu, "Intel Core i5-14500T", SensorKind.Temperature, "E-Core #1", 65f),
            new(SensorDevice.Cpu, "Intel Core i5-14500T", SensorKind.Clock, "E-Core #1", 3379f),
            new(SensorDevice.Cpu, "Intel Core i5-14500T", SensorKind.Clock, "Bus Speed", 99.8f),
            new(SensorDevice.GpuNvidia, "NVIDIA GeForce RTX 2070 SUPER", SensorKind.Temperature, "GPU Core", 71f),
            new(SensorDevice.GpuNvidia, "NVIDIA GeForce RTX 2070 SUPER", SensorKind.Data, "GPU Memory Used", 6451f),
            new(SensorDevice.GpuNvidia, "NVIDIA GeForce RTX 2070 SUPER", SensorKind.Data, "GPU Memory Total", 8192f),
        };

        [Fact]
        public void Расширенный_ИменаЯдраПамятьИМаксимумы()
        {
            var sensors = Intel(30f);
            var session = new OverlaySessionMax();
            session.Update(sensors);
            var text = ExtendedOverlay.Build(sensors, new FrameStats(141, 97, 7.1), new RamInfo(11.4, 31.9), session,
                session.Started.AddMinutes(42)).Select(l => l.Text).ToList();

            Assert.Equal("FPS 141 · 1% low 97 · 7.1 мс", text[0]);
            Assert.Contains("Intel Core i5-14500T", text);
            Assert.Contains("P1  69°  4675   E1  65°  3379", text);
            Assert.Contains("NVIDIA GeForce RTX 2070 SUPER", text);
            Assert.Contains("Видеопамять 6,3 ГБ из 8,0 ГБ", text);
            Assert.Contains("Оперативная память 11,4 ГБ из 31,9 ГБ", text);
            Assert.Contains("CPU 74 °C · GPU 71 °C", text);
            Assert.EndsWith("сеанс 0:42", text[^1]);
            Assert.DoesNotContain(text, t => t.Contains("предела"));
        }

        [Theory]
        [InlineData(3f, true)]
        [InlineData(12f, false)]
        public void Троттлинг_ПоЗапасуДоTjMax(float margin, bool warns)
        {
            var lines = ExtendedOverlay.Build(Intel(margin), new FrameStats(null, null, null), null,
                new OverlaySessionMax(), DateTime.Now);
            Assert.Equal(warns, lines.Any(l => l.Style == OverlayLineStyle.Warning));
        }

        [Fact]
        public void БезДрайвера_ЯдраНеПоказываются()
        {
            var sensors = new List<RawSensor>
            {
                new(SensorDevice.Cpu, "Intel Core i5-14500T", SensorKind.Load, "CPU Total", 12f),
                new(SensorDevice.Cpu, "Intel Core i5-14500T", SensorKind.Clock, "P-Core #1", null),
            };
            var text = ExtendedOverlay.Build(sensors, new FrameStats(null, null, null), null, new OverlaySessionMax(), DateTime.Now)
                .Select(l => l.Text).ToList();
            Assert.Equal("FPS —", text[0]);
            Assert.DoesNotContain(text, t => t.StartsWith("P1"));
        }

        private static List<RawSensor> Temps(float? cpu, float? gpu)
        {
            var list = new List<RawSensor>();
            list.Add(new(SensorDevice.Cpu, "CPU", SensorKind.Temperature, "CPU Package", cpu));
            list.Add(new(SensorDevice.GpuNvidia, "GPU", SensorKind.Temperature, "GPU Core", gpu));
            return list;
        }

        [Theory]
        [InlineData(91f, 60f, ThermalMood.Hot)]   // процессор горячий
        [InlineData(60f, 84f, ThermalMood.Hot)]   // видеокарта горячая
        [InlineData(64f, 71f, ThermalMood.Ok)]
        [InlineData(34f, 33f, ThermalMood.Cold)]
        [InlineData(34f, 55f, ThermalMood.Ok)]    // холодно только если холодно всё известное
        [InlineData(null, 35f, ThermalMood.Cold)] // без драйвера — по видеокарте
        [InlineData(null, null, ThermalMood.Ok)]  // температур нет — «нормально»
        public void НастроениеВенчика(float? cpu, float? gpu, ThermalMood expected)
        {
            Assert.Equal(expected, ExtendedOverlay.Mood(Temps(cpu, gpu)));
        }

        [Fact]
        public void НастроениеВенчика_УПределаTjMax_Жарко()
        {
            Assert.Equal(ThermalMood.Hot, ExtendedOverlay.Mood(Intel(3f)));
        }
}
}
