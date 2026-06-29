using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class VersionComparerTests
{
    [Theory]
    [InlineData("3.4.14", "3.4.13", 1)]
    [InlineData("3.4.13", "3.4.14", -1)]
    [InlineData("3.4.13", "3.4.13", 0)]
    [InlineData("3.4", "3.4.0", 0)]
    [InlineData("3.10.0", "3.9.99", 1)]
    [InlineData("3.4.14", "3.4.14-pre", 1)]
    [InlineData("3.4.14-beta", "3.4.14", -1)]
    public void Compare_ReturnsExpectedOrder(string left, string right, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(VersionComparer.Compare(left, right)));
    }

    [Theory]
    [InlineData("3.4.14", "3.4.13", true)]
    [InlineData("3.4.13", "3.4.13", false)]
    [InlineData("3.4.13-pre", "3.4.13", false)]
    public void IsNewer_UsesVersionOrdering(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, VersionComparer.IsNewer(candidate, current));
    }
}
