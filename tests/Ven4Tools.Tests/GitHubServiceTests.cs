using System.Collections.Generic;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class GitHubServiceTests
{
    [Theory]
    [InlineData("Ven4Tools.Setup-2.1.0.exe", true)]
    [InlineData("ven4tools.setup-2.1.0.exe", true)]
    [InlineData("Ven4Tools.Launcher-2.1.0.exe", false)]
    [InlineData("Ven4Tools.Setup-2.1.0.exe.sig", false)]
    [InlineData(null, false)]
    public void IsLauncherSetupAsset_MatchesOnlySetupExe(string? name, bool expected)
    {
        Assert.Equal(expected, GitHubService.IsLauncherSetupAsset(name));
    }

    [Theory]
    [InlineData("launcher-v2.1.0", "2.1.0")]
    [InlineData("v3.4.2", "3.4.2")]
    [InlineData("2.0.0", "2.0.0")]
    public void ParseVersionFromTag_StripsPrefixes(string tag, string expected)
    {
        Assert.Equal(expected, GitHubService.ParseVersionFromTag(tag));
    }

    [Fact]
    public void SelectLauncherUpdate_IgnoresReleasesWithoutSetupAsset()
    {
        var releases = new List<GitHubRelease>
        {
            new()
            {
                tag_name = "launcher-v2.1.0",
                prerelease = false,
                assets = new List<GitHubAsset>
                {
                    new() { name = "Ven4Tools.Launcher-2.1.0.exe", browser_download_url = "https://github.com/x/y/2.1.0-raw" }
                }
            },
            new()
            {
                tag_name = "launcher-v2.0.0",
                prerelease = false,
                assets = new List<GitHubAsset>
                {
                    new()
                    {
                        name = "Ven4Tools.Setup-2.0.0.exe",
                        browser_download_url = "https://github.com/x/y/2.0.0",
                        size = 123
                    }
                }
            }
        };

        var result = GitHubService.SelectLauncherUpdate(releases, currentVersion: "1.0.0");

        Assert.NotNull(result);
        Assert.Equal("2.0.0", result!.LatestVersion);
        Assert.Equal("https://github.com/x/y/2.0.0", result.DownloadUrl);
        Assert.True(result.HasUpdate);
    }

    [Fact]
    public void SelectLauncherUpdate_SkipsPrereleaseAndPicksNewestStableWithSetup()
    {
        var releases = new List<GitHubRelease>
        {
            new()
            {
                tag_name = "launcher-v3.0.0",
                prerelease = true,
                assets = new List<GitHubAsset>
                {
                    new() { name = "Ven4Tools.Setup-3.0.0.exe", browser_download_url = "https://github.com/x/y/3.0.0" }
                }
            },
            new()
            {
                tag_name = "launcher-v2.2.0",
                prerelease = false,
                assets = new List<GitHubAsset>
                {
                    new() { name = "Ven4Tools.Setup-2.2.0.exe", browser_download_url = "https://github.com/x/y/2.2.0" }
                }
            },
            new()
            {
                tag_name = "launcher-v2.1.0",
                prerelease = false,
                assets = new List<GitHubAsset>
                {
                    new() { name = "Ven4Tools.Setup-2.1.0.exe", browser_download_url = "https://github.com/x/y/2.1.0" }
                }
            }
        };

        var result = GitHubService.SelectLauncherUpdate(releases, currentVersion: "2.1.0");

        Assert.NotNull(result);
        Assert.Equal("2.2.0", result!.LatestVersion);
        Assert.True(result.HasUpdate);
    }

    [Fact]
    public void SelectLauncherUpdate_ReturnsNullWhenNoStableReleaseHasSetupAsset()
    {
        var releases = new List<GitHubRelease>
        {
            new()
            {
                tag_name = "v3.5.0",
                prerelease = false,
                assets = new List<GitHubAsset>
                {
                    new() { name = "Ven4Tools-Client-3.5.0.zip", browser_download_url = "https://github.com/x/y/client" }
                }
            }
        };

        var result = GitHubService.SelectLauncherUpdate(releases, currentVersion: "2.1.0");

        Assert.Null(result);
    }

    [Fact]
    public void SelectLauncherUpdate_HasUpdateFalseWhenCurrentIsAlreadyLatest()
    {
        var releases = new List<GitHubRelease>
        {
            new()
            {
                tag_name = "launcher-v2.1.0",
                prerelease = false,
                assets = new List<GitHubAsset>
                {
                    new() { name = "Ven4Tools.Setup-2.1.0.exe", browser_download_url = "https://github.com/x/y/2.1.0" }
                }
            }
        };

        var result = GitHubService.SelectLauncherUpdate(releases, currentVersion: "2.1.0");

        Assert.NotNull(result);
        Assert.False(result!.HasUpdate);
    }
}
