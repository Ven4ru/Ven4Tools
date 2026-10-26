using Ven4Tools.Services;

namespace Ven4Tools.Tests;

public sealed class MemoryDiagnosticsResultTests
{
    private static readonly DateTime When = new(2026, 10, 21, 9, 30, 0);

    [Theory]
    [InlineData(1201)]
    [InlineData(1101)]
    public void NoErrorsEvents(int id)
    {
        var r = MemoryDiagnosticsService.FromEvent(id, When);
        Assert.NotNull(r);
        Assert.False(r!.ErrorsFound);
        Assert.Contains("ошибок не обнаружено", MemoryDiagnosticsService.Describe(r));
    }

    [Theory]
    [InlineData(1202)]
    [InlineData(1102)]
    public void ErrorsEvents(int id)
    {
        var r = MemoryDiagnosticsService.FromEvent(id, When);
        Assert.NotNull(r);
        Assert.True(r!.ErrorsFound);
        Assert.Contains("обнаружены ошибки памяти", MemoryDiagnosticsService.Describe(r));
    }

    [Fact]
    public void InterruptedAndNotCompleted_HaveNoVerdict()
    {
        var interrupted = MemoryDiagnosticsService.FromEvent(1103, When);
        var notCompleted = MemoryDiagnosticsService.FromEvent(1104, When);
        Assert.Equal(MemoryTestOutcome.Interrupted, interrupted!.Outcome);
        Assert.Equal(MemoryTestOutcome.NotCompleted, notCompleted!.Outcome);
        Assert.False(interrupted.ErrorsFound);
        Assert.Contains("прерван", MemoryDiagnosticsService.Describe(interrupted));
        Assert.Contains("не удалось завершить", MemoryDiagnosticsService.Describe(notCompleted));
    }

    [Fact]
    public void OtherEvent_IsIgnored()
    {
        Assert.Null(MemoryDiagnosticsService.FromEvent(2001, When));
    }

    [Fact]
    public void NoResult_TestNotRun()
    {
        Assert.Equal("Тест не проводился", MemoryDiagnosticsService.Describe(null));
    }
}
