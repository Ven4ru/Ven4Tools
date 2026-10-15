using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Показания датчиков через LibreHardwareMonitorLib. Без драйвера видны видеокарты
    /// NVIDIA и AMD (через их драйверы), диски и загрузка процессора; температуры и
    /// частоты процессора и датчики платы — только при установленном PawnIO.
    ///
    /// Модули памяти (SPD) не опрашиваются: этот путь библиотеки умеет ходить через
    /// уже установленный в системе WinRing0, а с ним программа не работает ни в каком
    /// виде. Загрузка памяти и так видна в «Информации о системе».
    ///
    /// Библиотека открывается в фоне и один раз: с драйвером открытие занимает
    /// несколько секунд (опрос шин платы). После установки или удаления драйвера —
    /// <see cref="ResetAsync"/>, чтобы она увидела новое состояние.
    /// </summary>
    public sealed class HardwareSensorService
    {
        public static HardwareSensorService Instance { get; } = new();

        private readonly SemaphoreSlim _lock = new(1, 1);
        private Computer? _computer;
        private float? _maxClockMhz;

        private HardwareSensorService() { }

        public async Task<IReadOnlyList<SensorCard>> ReadAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                return await Task.Run(() =>
                {
                    _computer ??= Open();
                    var raw = Collect(_computer);
                    // Удалённый драйвер работает до перезагрузки, но для человека его уже нет.
                    bool driver = PawnIoDriver.IsInstalled() && PawnIoDriver.IsRunning();
                    // Оценка нужна и с драйвером: в виртуальных машинах и на части
                    // процессоров точные частоты ядер приходят нулями.
                    var estimate = EstimateCpuClock();
                    return HardwareSnapshotBuilder.Build(raw, driver, estimate);
                }, ct);
            }
            finally { _lock.Release(); }
        }

        /// <summary>Сырые показания — для расширенного оверлея.</summary>
        public async Task<IReadOnlyList<RawSensor>> ReadRawAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                return await Task.Run(() =>
                {
                    _computer ??= Open();
                    return (IReadOnlyList<RawSensor>)Collect(_computer);
                }, ct);
            }
            finally { _lock.Release(); }
        }

        /// <summary>Короткие строки для оверлея поверх игр.</summary>
        public async Task<IReadOnlyList<string>> ReadOverlayAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                return await Task.Run(() =>
                {
                    _computer ??= Open();
                    return HardwareSnapshotBuilder.OverlayLines(Collect(_computer));
                }, ct);
            }
            finally { _lock.Release(); }
        }

        /// <summary>Закрыть библиотеку: при следующем чтении она откроется заново.</summary>
        public async Task ResetAsync()
        {
            await _lock.WaitAsync();
            try
            {
                var computer = _computer;
                _computer = null;
                if (computer != null) await Task.Run(computer.Close);
            }
            catch (Exception ex) { AppLogger.Write(ex, "HardwareSensorService.Reset"); }
            finally { _lock.Release(); }
        }

        private static Computer Open()
        {
            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsStorageEnabled = true,
                IsMotherboardEnabled = true,
                IsMemoryEnabled = false,
                IsNetworkEnabled = false,
                IsControllerEnabled = false,
                IsPsuEnabled = false,
                IsBatteryEnabled = false
            };
            var started = DateTime.UtcNow;
            computer.Open();
            AppLogger.Write($"🌡 Датчики открыты за {(DateTime.UtcNow - started).TotalSeconds:0.0} с (драйвер датчиков: {(PawnIoDriver.IsRunning() ? "есть" : "нет")})");
            return computer;
        }

        private static List<RawSensor> Collect(Computer computer)
        {
            var result = new List<RawSensor>();
            void Walk(IHardware hw, SensorDevice device, string deviceName)
            {
                hw.Update();
                foreach (var sensor in hw.Sensors)
                {
                    var kind = sensor.SensorType switch
                    {
                        SensorType.Temperature => SensorKind.Temperature,
                        SensorType.Clock => SensorKind.Clock,
                        SensorType.Load => SensorKind.Load,
                        SensorType.Fan => SensorKind.Fan,
                        SensorType.Power => SensorKind.Power,
                        SensorType.SmallData => SensorKind.Data,
                        _ => SensorKind.Other
                    };
                    if (kind != SensorKind.Other)
                        result.Add(new RawSensor(device, deviceName, kind, sensor.Name, sensor.Value));
                }
                // Датчики Super I/O висят под платой отдельным устройством; имя берём его —
                // по нему видно контроллер (ITE, Nuvoton), а не модель платы ещё раз.
                foreach (var sub in hw.SubHardware) Walk(sub, device, device == SensorDevice.Board ? sub.Name : deviceName);
            }

            foreach (var hw in computer.Hardware)
            {
                var device = hw.HardwareType switch
                {
                    HardwareType.Cpu => SensorDevice.Cpu,
                    HardwareType.GpuNvidia => SensorDevice.GpuNvidia,
                    HardwareType.GpuAmd => SensorDevice.GpuAmd,
                    HardwareType.GpuIntel => SensorDevice.GpuIntel,
                    HardwareType.Storage => SensorDevice.Storage,
                    HardwareType.Motherboard => SensorDevice.Board,
                    _ => SensorDevice.Other
                };
                if (device == SensorDevice.Other) continue;
                try { Walk(hw, device, hw.Name); }
                catch (Exception ex) { AppLogger.Write(ex, $"HardwareSensorService: {hw.Name}"); }
            }
            return result;
        }

        // Без драйвера точной частоты нет, но Windows знает, на сколько процентов от
        // номинала работает процессор: номинал × «% Processor Performance» — та же
        // цифра, что «Скорость» в Диспетчере задач.
        private HardwareSnapshotBuilder.CpuClockEstimate? EstimateCpuClock()
        {
            try
            {
                if (_maxClockMhz == null)
                {
                    using var cpu = new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor");
                    _maxClockMhz = cpu.Get().Cast<ManagementObject>()
                        .Select(o => Convert.ToSingle(o["MaxClockSpeed"])).FirstOrDefault();
                }
                using var perf = new ManagementObjectSearcher(
                    "SELECT PercentProcessorPerformance FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name='_Total'");
                float percent = perf.Get().Cast<ManagementObject>()
                    .Select(o => Convert.ToSingle(o["PercentProcessorPerformance"])).FirstOrDefault();
                return _maxClockMhz > 0 && percent > 0
                    ? new HardwareSnapshotBuilder.CpuClockEstimate(_maxClockMhz.Value * percent / 100f)
                    : null;
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "HardwareSensorService.EstimateCpuClock");
                return null;
            }
        }
    }
}
