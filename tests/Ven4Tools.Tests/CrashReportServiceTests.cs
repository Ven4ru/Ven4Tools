using System.IO;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

public sealed class CrashReportServiceTests
{
    [Fact]
    public void LoadOrCreateDeviceId_CreatesRandomGuidNotDerivedFromMachineName()
    {
        using var dir = new TemporaryDirectory();
        string path = Path.Combine(dir.Path, "device_id.txt");

        string id = CrashReportService.LoadOrCreateDeviceId(path);

        Assert.True(Guid.TryParse(id, out _));
        Assert.True(File.Exists(path));
        Assert.NotEqual(Environment.MachineName, id, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadOrCreateDeviceId_IsStableAcrossCalls()
    {
        using var dir = new TemporaryDirectory();
        string path = Path.Combine(dir.Path, "device_id.txt");

        string first = CrashReportService.LoadOrCreateDeviceId(path);
        string second = CrashReportService.LoadOrCreateDeviceId(path);

        Assert.Equal(first, second);
    }

    [Fact]
    public void LoadOrCreateDeviceId_ReplacesCorruptedContentWithFreshGuid()
    {
        using var dir = new TemporaryDirectory();
        string path = Path.Combine(dir.Path, "device_id.txt");
        File.WriteAllText(path, "не-guid-мусор");

        string id = CrashReportService.LoadOrCreateDeviceId(path);

        Assert.True(Guid.TryParse(id, out _));
    }
}
