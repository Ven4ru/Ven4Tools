using Ven4Tools.Models;

namespace Ven4Tools.Tests;

public sealed class SourceOrderSettingsTests
{
    [Fact]
    public void DefaultOrder_PrefersVerifiedDirectDownloadBeforeCommunityManagers()
    {
        var settings = new SourceOrderSettings();

        Assert.Equal(
            new[]
            {
                SourceOrderSettings.Winget,
                SourceOrderSettings.Direct,
                SourceOrderSettings.Choco
            },
            settings.GlobalOrder);
    }
}
