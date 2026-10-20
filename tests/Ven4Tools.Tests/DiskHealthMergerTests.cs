using Ven4Tools.Services;

namespace Ven4Tools.Tests;

public sealed class DiskHealthMergerTests
{
    // Снимок с настоящего Samsung 990 PRO: Percentage Used 4, 70 512 455 единиц записи.
    private static DiskSmartData SamsungNvme() => new()
    {
        DriveNumber = 0,
        Model = "Samsung SSD 990 PRO 1TB",
        IsNvme = true,
        IsSsd = true,
        Status = SmartDiskStatus.Good,
        Temperature = 50,
        Life = 96,
        HostWritesGib = 33622,
        PowerOnCount = 708,
        PowerOnHours = 5803,
        Attributes = new[]
        {
            new SmartAttr(0x01, 0), new SmartAttr(0x03, 100), new SmartAttr(0x04, 10),
            new SmartAttr(0x05, 4), new SmartAttr(0x07, 70_512_455), new SmartAttr(0x0B, 708),
            new SmartAttr(0x0C, 5803), new SmartAttr(0x0D, 31), new SmartAttr(0x0E, 0),
            new SmartAttr(0x0F, 889) // журнал ошибок — не ошибки носителя, в вердикт не идёт
        }
    };

    private static WindowsDiskInfo WindowsNvme(int number = 0) => new()
    {
        Number = number, Model = "Samsung SSD 990 PRO 1TB", BusType = 17, MediaType = 4,
        SizeBytes = 1_000_204_886_016, Status = WindowsDiskStatus.Healthy,
        // Счётчики Windows на этом диске врут: износ 0 при настоящих 4 %.
        CounterWear = 0, CounterTemperature = 50
    };

    [Fact]
    public void Nvme_UsesHealthLog_AndPrefersSmartOverWindowsCounters()
    {
        var r = DiskHealthMerger.Merge(WindowsNvme(), SamsungNvme());
        Assert.Equal(DiskKind.Nvme, r.Kind);
        Assert.Equal(4, r.WearPercent);
        Assert.Equal(5803, r.PowerOnHours);
        Assert.Equal(708, r.PowerCycles);
        Assert.Equal(31, r.UnsafeShutdowns);
        Assert.Equal(0, r.MediaErrors);
        Assert.Equal(100, r.AvailableSpare);
        Assert.Equal(10, r.AvailableSpareThreshold);
        Assert.NotNull(r.WrittenTb);
        Assert.Equal(36.1, r.WrittenTb!.Value, 1); // 70 512 455 × 512 000 байт
        Assert.Equal(DiskVerdictLevel.Ok, DiskHealthVerdict.Evaluate(r).Level);
    }

    [Fact]
    public void SataSsd_WearFromLibraryLife_AndSectorCountersLow32Bits()
    {
        var smart = new DiskSmartData
        {
            DriveNumber = 1, Model = "CT500MX500SSD1", IsSsd = true, Life = 87, HostWritesGib = 10_000,
            PowerOnHours = 12000, PowerOnCount = 900,
            Attributes = new[]
            {
                new SmartAttr(0x05, 0x0001_0000_0002), // в старших байтах — постороннее
                new SmartAttr(0xC5, 0), new SmartAttr(0xC6, 0), new SmartAttr(0xAE, 42)
            }
        };
        var windows = new WindowsDiskInfo { Number = 1, Model = "CT500MX500SSD1", BusType = 11, MediaType = 4 };
        var r = DiskHealthMerger.Merge(windows, smart);
        Assert.Equal(DiskKind.SataSsd, r.Kind);
        Assert.Equal(13, r.WearPercent);
        Assert.Equal(2, r.Reallocated);
        Assert.Equal(0, r.Pending);
        Assert.Equal(42, r.UnsafeShutdowns);
        Assert.Equal(10.7, r.WrittenTb!.Value, 1);
    }

    [Fact]
    public void Hdd_HasNoWear_EvenIfLibraryGivesLife()
    {
        var smart = new DiskSmartData { DriveNumber = 2, Model = "ST2000DM008", Life = 100, Attributes = new[] { new SmartAttr(0x05, 0) } };
        var windows = new WindowsDiskInfo { Number = 2, Model = "ST2000DM008", BusType = 11, MediaType = 3 };
        var r = DiskHealthMerger.Merge(windows, smart);
        Assert.Equal(DiskKind.Hdd, r.Kind);
        Assert.Null(r.WearPercent);
    }

    [Fact]
    public void UsbWithoutSmart_FallsBackToWindowsCounters_ButStaysNoData()
    {
        var windows = new WindowsDiskInfo
        {
            Number = 3, Model = "USB DISK", BusType = 7, CounterTemperature = 0, CounterWear = 0, CounterPowerOnHours = null
        };
        var r = DiskHealthMerger.Merge(windows, null);
        Assert.Equal(DiskKind.Usb, r.Kind);
        Assert.Null(r.WearPercent);
        Assert.Null(r.TemperatureC);
        Assert.Equal(DiskVerdictLevel.NoData, DiskHealthVerdict.Evaluate(r).Level);
    }

    [Fact]
    public void WindowsCounters_UsedOnlyWhereSmartIsSilent()
    {
        var windows = new WindowsDiskInfo
        {
            Number = 4, Model = "Disk", MediaType = 4, CounterTemperature = 41, CounterPowerOnHours = 500,
            CounterWear = 12, CounterReadErrorsUncorrected = 0, CounterWriteErrorsUncorrected = 0
        };
        var r = DiskHealthMerger.Merge(windows, null);
        Assert.Equal(41, r.TemperatureC);
        Assert.Equal(500, r.PowerOnHours);
        Assert.Equal(12, r.WearPercent);
        Assert.Equal(0, r.Uncorrectable);
        Assert.Equal(DiskVerdictLevel.Ok, DiskHealthVerdict.Evaluate(r).Level);
    }

    [Fact]
    public void MergeAll_MatchesByDiskNumber_AndKeepsDisksOnlySmartSees()
    {
        var all = DiskHealthMerger.MergeAll(
            new[] { WindowsNvme(0) },
            new[] { SamsungNvme(), SamsungNvme() with { DriveNumber = 5, Model = "RAID member" } });
        Assert.Equal(2, all.Count);
        Assert.Equal(4, all[0].WearPercent);
        Assert.Equal(5, all[1].Number);
    }

    [Fact]
    public void GarbageTemperature_IsIgnored()
    {
        var smart = SamsungNvme() with { Temperature = 323 };
        Assert.Equal(50, DiskHealthMerger.Merge(WindowsNvme(), smart).TemperatureC); // взята из счётчиков Windows
        Assert.Null(DiskHealthMerger.Merge(null, smart).TemperatureC);
    }
}
