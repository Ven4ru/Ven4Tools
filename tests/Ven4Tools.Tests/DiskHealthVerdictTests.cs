using Ven4Tools.Services;

namespace Ven4Tools.Tests;

public sealed class DiskHealthVerdictTests
{
    private static DiskHealthReading Nvme(
        int? wear = 4, long? media = 0, int? critical = 0, int? spare = 100, int? spareThreshold = 10,
        int? temp = 45, WindowsDiskStatus windows = WindowsDiskStatus.Healthy,
        SmartDiskStatus smart = SmartDiskStatus.Good) => new()
    {
        Number = 0,
        Model = "Samsung SSD 990 PRO 1TB",
        Kind = DiskKind.Nvme,
        WearPercent = wear,
        MediaErrors = media,
        NvmeCriticalWarning = critical,
        AvailableSpare = spare,
        AvailableSpareThreshold = spareThreshold,
        TemperatureC = temp,
        WindowsStatus = windows,
        SmartStatus = smart
    };

    private static DiskHealthReading Ata(
        DiskKind kind = DiskKind.Hdd, int? wear = null, long? reallocated = 0, long? pending = 0,
        long? uncorrectable = 0, int? temp = 35) => new()
    {
        Number = 1,
        Model = "WDC WD10EZEX",
        Kind = kind,
        WearPercent = wear,
        Reallocated = reallocated,
        Pending = pending,
        Uncorrectable = uncorrectable,
        TemperatureC = temp
    };

    [Fact]
    public void HealthyNvme_IsOk_WithWearInReason()
    {
        var v = DiskHealthVerdict.Evaluate(Nvme());
        Assert.Equal(DiskVerdictLevel.Ok, v.Level);
        Assert.Contains("Износ 4%", v.Reasons);
        Assert.Contains("Ошибок не найдено", v.Reasons);
    }

    [Theory]
    [InlineData(69, DiskVerdictLevel.Ok)]
    [InlineData(70, DiskVerdictLevel.Watch)]
    [InlineData(89, DiskVerdictLevel.Watch)]
    [InlineData(90, DiskVerdictLevel.Backup)]
    [InlineData(150, DiskVerdictLevel.Backup)]
    public void Wear_Thresholds(int wear, DiskVerdictLevel expected)
    {
        Assert.Equal(expected, DiskHealthVerdict.Evaluate(Nvme(wear: wear)).Level);
    }

    [Fact]
    public void NvmeMediaErrors_MeanBackup()
    {
        var v = DiskHealthVerdict.Evaluate(Nvme(media: 3));
        Assert.Equal(DiskVerdictLevel.Backup, v.Level);
        Assert.Contains(v.Reasons, r => r.Contains("Ошибки носителя"));
    }

    [Fact]
    public void SpareAtThreshold_MeansBackup()
    {
        Assert.Equal(DiskVerdictLevel.Backup, DiskHealthVerdict.Evaluate(Nvme(spare: 10, spareThreshold: 10)).Level);
        Assert.Equal(DiskVerdictLevel.Ok, DiskHealthVerdict.Evaluate(Nvme(spare: 11, spareThreshold: 10)).Level);
    }

    [Theory]
    [InlineData(0x01, DiskVerdictLevel.Backup)]
    [InlineData(0x04, DiskVerdictLevel.Backup)]
    [InlineData(0x08, DiskVerdictLevel.Backup)]
    [InlineData(0x02, DiskVerdictLevel.Watch)]
    public void NvmeCriticalWarningBits(int bits, DiskVerdictLevel expected)
    {
        Assert.Equal(expected, DiskHealthVerdict.Evaluate(Nvme(critical: bits)).Level);
    }

    [Theory]
    [InlineData(0, 0, 0, DiskVerdictLevel.Ok)]
    [InlineData(1, 0, 0, DiskVerdictLevel.Watch)]
    [InlineData(99, 0, 0, DiskVerdictLevel.Watch)]
    [InlineData(100, 0, 0, DiskVerdictLevel.Backup)]
    [InlineData(0, 1, 0, DiskVerdictLevel.Backup)]
    [InlineData(0, 0, 1, DiskVerdictLevel.Backup)]
    public void AtaSectorCounters(long reallocated, long pending, long uncorrectable, DiskVerdictLevel expected)
    {
        var v = DiskHealthVerdict.Evaluate(Ata(reallocated: reallocated, pending: pending, uncorrectable: uncorrectable));
        Assert.Equal(expected, v.Level);
    }

    [Fact]
    public void HotHdd_IsWatch_ButSameTemperatureForSsdIsOk()
    {
        Assert.Equal(DiskVerdictLevel.Watch, DiskHealthVerdict.Evaluate(Ata(temp: 56)).Level);
        Assert.Equal(DiskVerdictLevel.Ok, DiskHealthVerdict.Evaluate(Nvme(temp: 56)).Level);
        Assert.Equal(DiskVerdictLevel.Watch, DiskHealthVerdict.Evaluate(Nvme(temp: 72)).Level);
    }

    [Fact]
    public void UsbWithoutSmart_IsNoData_NotOk()
    {
        var usb = new DiskHealthReading
        {
            Number = 2, Model = "JMicron USB", Kind = DiskKind.Usb,
            TemperatureC = 30, PowerOnHours = 100,
            WindowsStatus = WindowsDiskStatus.Healthy
        };
        var v = DiskHealthVerdict.Evaluate(usb);
        Assert.Equal(DiskVerdictLevel.NoData, v.Level);
        Assert.Equal("нет данных", v.Label);
        Assert.Contains(v.Reasons, r => r.Contains("USB"));
    }

    [Fact]
    public void NoSmart_ButWindowsUnhealthy_IsBackup()
    {
        var d = new DiskHealthReading { Number = 3, Model = "X", Kind = DiskKind.Other, WindowsStatus = WindowsDiskStatus.Unhealthy };
        Assert.Equal(DiskVerdictLevel.Backup, DiskHealthVerdict.Evaluate(d).Level);
    }

    [Fact]
    public void SmartBad_RaisesLevel_EvenWithoutOwnReasons()
    {
        var v = DiskHealthVerdict.Evaluate(Nvme(smart: SmartDiskStatus.Bad));
        Assert.Equal(DiskVerdictLevel.Backup, v.Level);
        Assert.Equal(new[] { "Оценка SMART: плохо" }, v.Reasons);
    }

    [Fact]
    public void SmartCaution_DoesNotDuplicateOwnReason()
    {
        var v = DiskHealthVerdict.Evaluate(Ata(reallocated: 5) with { SmartStatus = SmartDiskStatus.Caution });
        Assert.Equal(DiskVerdictLevel.Watch, v.Level);
        Assert.Single(v.Reasons);
    }

    [Fact]
    public void Format_HddWear_NotApplicable_AndUnknownIsNoData()
    {
        Assert.Equal("у HDD не считается", DiskHealthFormat.Wear(Ata()));
        Assert.Equal("нет данных", DiskHealthFormat.Wear(Ata(kind: DiskKind.SataSsd)));
        Assert.Equal("нет данных", DiskHealthFormat.Temperature(Ata(temp: null)));
    }
}
