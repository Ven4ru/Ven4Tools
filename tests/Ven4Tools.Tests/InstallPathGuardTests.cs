using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class InstallPathGuardTests
{
    [Theory]
    [InlineData(@"C:\Ven4Tools\Ven4Tools_Client", @"C:\Users\test\AppData\Local\Ven4Tools", true)]
    [InlineData(@"C:\Users\test\AppData\Local\Ven4Tools", @"C:\Users\test\AppData\Local\Ven4Tools", false)]
    [InlineData(@"C:\Users\test\AppData\Local\Ven4Tools\Client", @"C:\Users\test\AppData\Local\Ven4Tools", false)]
    [InlineData(@"C:\Users\test\AppData\Local\Ven4Tools", @"C:\Users\test\AppData\Local\Ven4Tools\Client", false)]
    [InlineData(@"C:\Users\test\AppData\Local\ven4tools", @"C:\Users\test\AppData\Local\Ven4Tools", false)]
    [InlineData(@"C:\Users\test\AppData\Local\Ven4ToolsExtra", @"C:\Users\test\AppData\Local\Ven4Tools", true)]
    public void IsClientPathSafe_DetectsOverlapWithDataFolder(string clientPath, string dataFolderPath, bool expectedSafe)
    {
        Assert.Equal(expectedSafe, InstallPathGuard.IsClientPathSafe(clientPath, dataFolderPath));
    }
}
