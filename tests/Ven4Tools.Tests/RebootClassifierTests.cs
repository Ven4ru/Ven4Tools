using Ven4Tools.Services;

namespace Ven4Tools.Tests;

public sealed class RebootClassifierTests
{
    private static RebootEvent MakeEvent(
        int bugcheckCode = 0,
        bool fastStartupNearby = false,
        bool cleanShutdownNearby = false) => new()
    {
        TimeCreated = new DateTime(2026, 7, 21, 7, 54, 0),
        BugcheckCode = bugcheckCode,
        HasFastStartupFailureNearby = fastStartupNearby,
        HasCleanShutdownNearby = cleanShutdownNearby
    };

    [Fact]
    public void Classify_CleanShutdownNearby_ReturnsNull()
    {
        var evt = MakeEvent(cleanShutdownNearby: true);
        Assert.Null(RebootClassifier.Classify(evt));
    }

    [Fact]
    public void Classify_NonZeroBugcheck_ReturnsBsod()
    {
        var evt = MakeEvent(bugcheckCode: 0x139);
        Assert.Equal(RebootCategory.Bsod, RebootClassifier.Classify(evt));
    }

    [Fact]
    public void Classify_NonZeroBugcheckAndFastStartupNearby_StillReturnsBsod()
    {
        // Настоящий BSOD должен побеждать даже если рядом случайно оказалось
        // событие 29 — приоритет BSOD выше, порядок проверки в Classify важен.
        var evt = MakeEvent(bugcheckCode: 0x139, fastStartupNearby: true);
        Assert.Equal(RebootCategory.Bsod, RebootClassifier.Classify(evt));
    }

    [Fact]
    public void Classify_ZeroBugcheckWithFastStartupNearby_ReturnsFastStartupFailure()
    {
        var evt = MakeEvent(bugcheckCode: 0, fastStartupNearby: true);
        Assert.Equal(RebootCategory.FastStartupFailure, RebootClassifier.Classify(evt));
    }

    [Fact]
    public void Classify_ZeroBugcheckNoFastStartupNoCleanShutdown_ReturnsPossiblePowerLoss()
    {
        var evt = MakeEvent();
        Assert.Equal(RebootCategory.PossiblePowerLoss, RebootClassifier.Classify(evt));
    }
}
