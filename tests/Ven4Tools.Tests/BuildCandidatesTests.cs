using System.Net.Http;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class BuildCandidatesTests
{
    private const string CdnUrl = "https://cdn.ven4tools.ru/releases/x.zip";
    private const string HostingUrl = "https://ven4tools.ru/releases/x.zip";
    private const string GithubUrl = "https://github.com/Ven4ru/Ven4Tools/releases/download/x.zip";

    private static readonly HttpClient Normal = new();
    private static readonly HttpClient Pinned = new();

    private static List<DownloadCandidate> Build(DownloadSource preference) =>
        FallbackDownloader.BuildCandidates(preference, CdnUrl, HostingUrl, GithubUrl, Normal, Pinned);

    [Fact]
    public void Auto_UsesCdnDomainCdnIpHostingGithubOrder()
    {
        var result = Build(DownloadSource.Auto);

        Assert.Equal(4, result.Count);
        Assert.Equal("CDN", result[0].SourceLabel);
        Assert.Same(Normal, result[0].Client);
        Assert.Equal(CdnUrl, result[0].Url);

        Assert.Equal("CDN (прямой IP)", result[1].SourceLabel);
        Assert.Same(Pinned, result[1].Client);
        Assert.Equal(CdnUrl, result[1].Url); // та же ссылка, другой транспорт

        Assert.Equal("Хостинг", result[2].SourceLabel);
        Assert.Equal(HostingUrl, result[2].Url);

        Assert.Equal("GitHub", result[3].SourceLabel);
        Assert.Equal(GithubUrl, result[3].Url);
    }

    [Fact]
    public void Github_MovesGithubToFrontKeepingRestOrder()
    {
        var result = Build(DownloadSource.Github);

        Assert.Equal(new[] { "GitHub", "CDN", "CDN (прямой IP)", "Хостинг" },
            result.Select(c => c.SourceLabel).ToArray());
    }

    [Fact]
    public void HostingMirror_MovesHostingToFront()
    {
        var result = Build(DownloadSource.HostingMirror);

        Assert.Equal(new[] { "Хостинг", "CDN", "CDN (прямой IP)", "GitHub" },
            result.Select(c => c.SourceLabel).ToArray());
    }

    [Fact]
    public void CdnDirectIp_MovesDirectIpToFront()
    {
        var result = Build(DownloadSource.CdnDirectIp);

        Assert.Equal(new[] { "CDN (прямой IP)", "CDN", "Хостинг", "GitHub" },
            result.Select(c => c.SourceLabel).ToArray());
        Assert.Same(Pinned, result[0].Client);
    }

    [Fact]
    public void CdnDomain_KeepsCdnDomainFirst()
    {
        var result = Build(DownloadSource.CdnDomain);

        Assert.Equal(new[] { "CDN", "CDN (прямой IP)", "Хостинг", "GitHub" },
            result.Select(c => c.SourceLabel).ToArray());
    }

    [Fact]
    public void SkipsNullAndEmptyUrls()
    {
        // CDN недоступен (нет CDN/зеркала) — остаётся только GitHub.
        var result = FallbackDownloader.BuildCandidates(
            DownloadSource.Auto, cdnUrl: null, cdnMirrorHostingUrl: "  ", githubUrl: GithubUrl, Normal, Pinned);

        Assert.Single(result);
        Assert.Equal("GitHub", result[0].SourceLabel);
    }

    [Fact]
    public void EmptyWhenNoUrlsProvided()
    {
        var result = FallbackDownloader.BuildCandidates(
            DownloadSource.Auto, cdnUrl: null, cdnMirrorHostingUrl: null, githubUrl: null, Normal, Pinned);

        Assert.Empty(result);
    }

    [Fact]
    public void DoesNotDuplicateIdenticalUrlAndClientPair()
    {
        // Зеркало указывает на тот же URL с тем же (обычным) клиентом, что CDN-домен —
        // дубль (Url, Client) не добавляется. CDN(прямой IP) — тот же URL, но другой
        // клиент, поэтому остаётся отдельным кандидатом.
        var result = FallbackDownloader.BuildCandidates(
            DownloadSource.Auto, CdnUrl, cdnMirrorHostingUrl: CdnUrl, githubUrl: null, Normal, Pinned);

        Assert.Equal(2, result.Count);
        Assert.Equal("CDN", result[0].SourceLabel);
        Assert.Equal("CDN (прямой IP)", result[1].SourceLabel);
    }
}
