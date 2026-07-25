using Ven4Tools.Models;
using Ven4Tools.Services.DiskBenchmark;

namespace Ven4Tools.Tests;

public class BenchmarkReportBuilderTests
{
    private static BenchmarkRunResult BuildResult(PciLinkInfo link)
    {
        var disk = new PhysicalDiskInfo
        {
            Index = 0,
            FriendlyName = "Тестовый накопитель",
            SizeBytes = 1_000_204_886_016,
            Bus = DiskBusKind.Nvme,
            Media = DiskMediaKind.Ssd,
            Link = link
        };

        var result = new BenchmarkRunResult
        {
            Disk = disk,
            VolumeLetter = "D:",
            Profile = BenchmarkProfile.Normal,
            Passes = 3,
            FileSizeBytes = 1024L * 1024 * 1024,
            StartedAt = new DateTime(2026, 7, 25, 14, 0, 0),
            Duration = TimeSpan.FromSeconds(161)
        };

        foreach (var pattern in DiskBenchmarkEngine.Patterns)
        {
            result.Measurements.Add(new BenchmarkMeasurement
            {
                PatternName = pattern.Name,
                Operation = BenchmarkOperation.Read,
                MegabytesPerSecond = pattern.Name == "SEQ1M Q8T1" ? 6543.2 : 100,
                OperationsPerSecond = 6240,
                AverageLatencyMicroseconds = 1282
            });
            result.Measurements.Add(new BenchmarkMeasurement
            {
                PatternName = pattern.Name,
                Operation = BenchmarkOperation.Write,
                MegabytesPerSecond = 5120.4,
                OperationsPerSecond = 4883,
                AverageLatencyMicroseconds = 1638
            });
        }

        return result;
    }

    [Fact]
    public void ПриНеизвестнойЛинии_ПотолокИДоляНеПоказываются()
    {
        string report = BenchmarkReportBuilder.Build(BuildResult(PciLinkInfo.Unknown));

        Assert.DoesNotContain("Потолок", report);
        Assert.DoesNotContain("пропускной способности интерфейса", report);
        Assert.Contains("не определяются", report);
    }

    [Fact]
    public void ПриИзвестнойЛинии_ПоказываютсяПотолокИДоляЕгоИспользования()
    {
        var link = new PciLinkInfo { Generation = 4, Width = 4 };
        string report = BenchmarkReportBuilder.Build(BuildResult(link));

        Assert.Contains("PCIe 4.0 x4", report);
        Assert.Contains("Потолок", report);
        Assert.Contains("пропускной способности интерфейса", report);
    }

    [Fact]
    public void ОтчётСодержитВсеЧетыреПаттерна()
    {
        string report = BenchmarkReportBuilder.Build(BuildResult(PciLinkInfo.Unknown));

        foreach (var pattern in DiskBenchmarkEngine.Patterns)
            Assert.Contains(pattern.Name, report);
    }

    [Fact]
    public void ОтменённыйПрогон_ПомеченКакНеполный()
    {
        var result = BuildResult(PciLinkInfo.Unknown);
        result.Cancelled = true;

        string report = BenchmarkReportBuilder.Build(result);

        Assert.Contains("остановлен", report);
    }

    [Fact]
    public void Уровень_ОписываетсяКакСопоставимость_АНеКакФакт()
    {
        Assert.Contains("сопоставимо", BenchmarkReportBuilder.DescribeLevel(6543));
        Assert.Contains("сопоставимо", BenchmarkReportBuilder.DescribeLevel(120));
    }

    [Fact]
    public void ПодключениеSata_НеВыдумываетРевизию()
    {
        var disk = new PhysicalDiskInfo { Bus = DiskBusKind.Sata, Media = DiskMediaKind.Ssd };

        string description = BenchmarkReportBuilder.DescribeConnection(disk);

        Assert.Contains("SATA", description);
        Assert.Contains("не определяется", description);
        Assert.DoesNotContain("6", description);
    }

    [Fact]
    public void ПодключениеNvmeБезДанныхОЛинии_ЧестноГоворитЧтоНеизвестно()
    {
        var disk = new PhysicalDiskInfo { Bus = DiskBusKind.Nvme, Link = PciLinkInfo.Unknown };

        string description = BenchmarkReportBuilder.DescribeConnection(disk);

        Assert.Contains("NVMe", description);
        Assert.Contains("не определяются", description);
    }

    [Fact]
    public void ОтчётНеСодержитСетевыхАдресов()
    {
        string report = BenchmarkReportBuilder.Build(BuildResult(new PciLinkInfo { Generation = 4, Width = 4 }));

        Assert.DoesNotContain("http", report);
        Assert.DoesNotContain("ven4tools.ru", report);
    }
}
