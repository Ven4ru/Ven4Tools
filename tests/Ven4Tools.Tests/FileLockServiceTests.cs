using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// «Кто держит файл»: каким процессам нельзя показывать кнопку «Завершить».
/// </summary>
public sealed class FileLockServiceTests
{
    [Theory]
    [InlineData("System", 4)]
    [InlineData("Idle", 0)]
    [InlineData("csrss", 700)]
    [InlineData("csrss.exe", 700)]
    [InlineData("svchost", 1200)]
    [InlineData("SVCHOST.EXE", 1200)]
    [InlineData("lsass", 900)]
    [InlineData("winlogon", 800)]
    [InlineData("dwm", 1500)]
    public void CoreWindowsProcessesAreSystem(string name, int pid)
    {
        Assert.True(FileLockService.IsSystemProcess(name, pid, sessionId: 1, isCritical: false, isService: false));
    }

    [Theory]
    [InlineData("WINWORD")]
    [InlineData("notepad.exe")]
    [InlineData("explorer")]
    [InlineData("Telegram")]
    public void UserProgramsCanBeTerminated(string name)
    {
        Assert.False(FileLockService.IsSystemProcess(name, 5000, sessionId: 1, isCritical: false, isService: false));
    }

    [Fact]
    public void ServicesAndCriticalProcessesAreSystem()
    {
        Assert.True(FileLockService.IsSystemProcess("SomeAgent", 5000, sessionId: 1, isCritical: false, isService: true));
        Assert.True(FileLockService.IsSystemProcess("SomeAgent", 5000, sessionId: 1, isCritical: true, isService: false));
    }

    [Fact]
    public void SessionZeroProcessesAreSystem()
    {
        Assert.True(FileLockService.IsSystemProcess("SomeAgent", 5000, sessionId: 0, isCritical: false, isService: false));
    }

    [Fact]
    public void MissingPathThrows()
    {
        string path = Path.Combine(Path.GetTempPath(), "v4t-no-such-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<FileNotFoundException>(() => FileLockService.Find(path, CancellationToken.None));
    }
}
