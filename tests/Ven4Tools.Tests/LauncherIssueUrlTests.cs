using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Ссылка на созданный issue приходит из ответа API сайта и уходит в
/// Process.Start(UseShellExecute=true) — открывать можно только https-страницу
/// issue нашего репозитория на github.com.
/// </summary>
public sealed class LauncherIssueUrlTests
{
    [Theory]
    [InlineData("https://github.com/Ven4ru/Ven4Tools/issues/123")]
    [InlineData("https://GitHub.com/Ven4ru/Ven4Tools/issues/1")]
    public void IsTrustedIssueUrl_ПринимаетIssueРепозитория(string url)
    {
        Assert.True(GitHubService.IsTrustedIssueUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("http://github.com/Ven4ru/Ven4Tools/issues/1")]
    [InlineData("https://github.com.evil.example/Ven4ru/Ven4Tools/issues/1")]
    [InlineData("https://github.com:8443/Ven4ru/Ven4Tools/issues/1")]
    [InlineData("https://github.com/someone/else/issues/1")]
    [InlineData("https://user@evil.example/Ven4ru/Ven4Tools/issues/1")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    public void IsTrustedIssueUrl_ОтклоняетВсёОстальное(string? url)
    {
        Assert.False(GitHubService.IsTrustedIssueUrl(url));
    }
}
