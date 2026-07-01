using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class LauncherUpdateSecurityTests
{
    [Theory]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", true)]
    [InlineData("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", true)]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("za7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", false)]
    public void IsValidSha256_RequiresCompleteHexDigest(string value, bool expected)
    {
        Assert.Equal(expected, LauncherUpdateService.IsValidSha256(value));
    }
}
