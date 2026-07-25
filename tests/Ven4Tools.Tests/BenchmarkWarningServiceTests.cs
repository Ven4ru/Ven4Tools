using Ven4Tools.Models;
using Ven4Tools.Services.DiskBenchmark;

namespace Ven4Tools.Tests;

public class BenchmarkWarningServiceTests
{
    [Fact]
    public void ЧистыйНесистемныйТом_БезПредупреждений()
    {
        var warnings = BenchmarkWarningService.Build(
            isSystemVolume: false, usedPercent: 40, bitLocker: false, removable: false);

        Assert.Empty(warnings);
    }

    [Fact]
    public void ЗаполненныйБолее90Процентов_ДаётПредупреждение()
    {
        var warnings = BenchmarkWarningService.Build(
            isSystemVolume: false, usedPercent: 93, bitLocker: false, removable: false);

        Assert.Contains(warnings, w => w.Contains("заполнен"));
    }

    [Fact]
    public void РовноНаГранице90Процентов_ЕщёНеПредупреждает()
    {
        var warnings = BenchmarkWarningService.Build(
            isSystemVolume: false, usedPercent: 90, bitLocker: false, removable: false);

        Assert.Empty(warnings);
    }

    [Fact]
    public void СистемныйТом_ДаётПредупреждение()
    {
        var warnings = BenchmarkWarningService.Build(
            isSystemVolume: true, usedPercent: 10, bitLocker: false, removable: false);

        Assert.Contains(warnings, w => w.Contains("системн"));
    }

    [Fact]
    public void BitLocker_ДаётПредупреждение()
    {
        var warnings = BenchmarkWarningService.Build(
            isSystemVolume: false, usedPercent: 10, bitLocker: true, removable: false);

        Assert.Contains(warnings, w => w.Contains("BitLocker"));
    }

    [Fact]
    public void СъёмныйНакопитель_ДаётПредупреждение()
    {
        var warnings = BenchmarkWarningService.Build(
            isSystemVolume: false, usedPercent: 10, bitLocker: false, removable: true);

        Assert.Contains(warnings, w => w.Contains("порт"));
    }

    [Fact]
    public void НедостаточноМеста_БлокируетЗапуск()
    {
        var volume = new BenchmarkVolumeInfo
        {
            Letter = "D:",
            IsReady = true,
            TotalBytes = 100L * 1024 * 1024 * 1024,
            // Свободно ровно столько же, сколько нужен файл — без обязательного запаса.
            FreeBytes = 1024L * 1024 * 1024
        };

        bool allowed = BenchmarkWarningService.TryValidateFreeSpace(
            volume, 1024L * 1024 * 1024, out string error);

        Assert.False(allowed);
        Assert.Contains("места", error);
    }

    [Fact]
    public void МестаХватает_ЗапускРазрешён()
    {
        var volume = new BenchmarkVolumeInfo
        {
            Letter = "D:",
            IsReady = true,
            TotalBytes = 100L * 1024 * 1024 * 1024,
            FreeBytes = 50L * 1024 * 1024 * 1024
        };

        bool allowed = BenchmarkWarningService.TryValidateFreeSpace(
            volume, 1024L * 1024 * 1024, out string error);

        Assert.True(allowed);
        Assert.Equal("", error);
    }

    [Fact]
    public void НеготовыйТом_БлокируетЗапуск()
    {
        var volume = new BenchmarkVolumeInfo { Letter = "E:", IsReady = false };

        bool allowed = BenchmarkWarningService.TryValidateFreeSpace(
            volume, 1024L * 1024 * 1024, out string error);

        Assert.False(allowed);
        Assert.NotEqual("", error);
    }
}
