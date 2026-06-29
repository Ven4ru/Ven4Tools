using System.Net;
using ClientDownloadValidator = Ven4Tools.Services.DownloadValidator;
using LauncherDownloadValidator = Ven4Tools.Launcher.Services.DownloadValidator;

namespace Ven4Tools.Tests;

public sealed class DownloadValidatorTests
{
    [Theory]
    [InlineData("https://github.com/Ven4ru/Ven4Tools/releases/download/file.zip", true)]
    [InlineData("https://objects.githubusercontent.com/file", true)]
    [InlineData("https://cdn.ven4tools.ru/file.zip", true)]
    [InlineData("https://download.microsoft.com/file.exe", true)]
    [InlineData("http://github.com/file.zip", false)]
    [InlineData("https://github.com.evil.example/file.zip", false)]
    [InlineData("https://evil.example/file.zip", false)]
    [InlineData("", false)]
    [InlineData("not-a-url", false)]
    public void LauncherAllowlist_RejectsUntrustedLocations(string url, bool expected)
    {
        Assert.Equal(expected, LauncherDownloadValidator.IsAllowedDownloadHost(url));
    }

    [Theory]
    [InlineData("https://vendor.example/setup.exe", true)]
    [InlineData("http://vendor.example/setup.exe", false)]
    [InlineData("file:///C:/setup.exe", false)]
    [InlineData("not-a-url", false)]
    public void ClientValidator_RequiresHttps(string url, bool expected)
    {
        Assert.Equal(expected, ClientDownloadValidator.ValidateUrl(url));
    }

    [Fact]
    public void RedirectValidation_UsesFinalRequestUri()
    {
        using var allowed = ResponseWithFinalUri("https://cdn.ven4tools.ru/file.zip");
        using var rejected = ResponseWithFinalUri("https://attacker.example/file.zip");

        Assert.True(LauncherDownloadValidator.IsAllowedDownloadHostAfterRedirect(allowed));
        Assert.False(LauncherDownloadValidator.IsAllowedDownloadHostAfterRedirect(rejected));
        Assert.True(ClientDownloadValidator.ValidateAfterRedirect(rejected));
    }

    private static HttpResponseMessage ResponseWithFinalUri(string uri)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri)
        };
    }
}
