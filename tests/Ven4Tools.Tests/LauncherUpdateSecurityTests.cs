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

    [Fact]
    public void BuildSetupUpdateArguments_ContainsSilentUpdateWaitAndRelaunchFlags()
    {
        string args = LauncherUpdateService.BuildSetupUpdateArguments(waitPid: 4242);

        Assert.Contains("/S", args);
        Assert.Contains("/UPDATE", args);
        Assert.Contains("/WAITPID=4242", args);
        Assert.Contains("/RELAUNCH", args);
    }

    [Theory]
    [InlineData("2.1.0", "Ven4Tools.Setup-2.1.0.exe")]
    [InlineData("10.20.30", "Ven4Tools.Setup-10.20.30.exe")]
    public void BuildSetupFileName_ProducesExpectedName(string version, string expected)
    {
        Assert.Equal(expected, LauncherUpdateService.BuildSetupFileName(version));
    }

    [Fact]
    public void BuildSetupFileName_SanitizesPathSeparatorsFromUntrustedVersionString()
    {
        // Версия приходит из тега GitHub-релиза — не доверенные данные.
        // Разделители пути не должны попасть в итоговое имя файла, иначе
        // комбинация Path.Combine(stagingDir, fileName) могла бы уйти
        // за пределы временной папки.
        string name = LauncherUpdateService.BuildSetupFileName("1.0.0/../evil");

        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain("\\", name);
    }
}
