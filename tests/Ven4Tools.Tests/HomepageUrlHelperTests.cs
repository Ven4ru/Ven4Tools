using Ven4Tools.Services;

namespace Ven4Tools.Tests;

public sealed class HomepageUrlHelperTests
{
    [Theory]
    [InlineData("https://download.mozilla.org/?product=firefox-latest&os=win64", "https://download.mozilla.org")]
    [InlineData("https://dl.google.com/chrome/install/latest/chrome_installer.exe", "https://dl.google.com")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("not a url", null)]
    public void ExtractHomepage_ReturnsSchemeAndHost(string? downloadUrl, string? expected)
    {
        Assert.Equal(expected, HomepageUrlHelper.ExtractHomepage(downloadUrl));
    }
}
