using System.Net.Http;
using System.Net.Sockets;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class CdnServiceTests
{
    [Fact]
    public void IsDnsResolutionFailure_TrueForNameResolutionError()
    {
        // Прямой сигнал .NET 8 о неудачном резолвинге DNS-имени.
        var ex = new HttpRequestException(HttpRequestError.NameResolutionError, "имя не разрешено");
        Assert.True(CdnService.IsDnsResolutionFailure(ex));
    }

    [Fact]
    public void IsDnsResolutionFailure_TrueForWrappedHostNotFoundSocketException()
    {
        var socket = new SocketException((int)SocketError.HostNotFound);
        var ex = new HttpRequestException("обёртка", socket);
        Assert.True(CdnService.IsDnsResolutionFailure(ex));
    }

    [Theory]
    [InlineData(SocketError.TryAgain)]
    [InlineData(SocketError.NoData)]
    public void IsDnsResolutionFailure_TrueForOtherDnsSocketErrors(SocketError code)
    {
        var ex = new HttpRequestException("обёртка", new SocketException((int)code));
        Assert.True(CdnService.IsDnsResolutionFailure(ex));
    }

    [Fact]
    public void IsDnsResolutionFailure_FalseForGenericHttpError()
    {
        // Обычная сетевая ошибка (не DNS) — IP-fallback не запускается, тихо на GitHub.
        Assert.False(CdnService.IsDnsResolutionFailure(new HttpRequestException("HTTP 500")));
    }

    [Fact]
    public void IsDnsResolutionFailure_FalseForConnectionRefused()
    {
        var ex = new HttpRequestException("обёртка", new SocketException((int)SocketError.ConnectionRefused));
        Assert.False(CdnService.IsDnsResolutionFailure(ex));
    }

    [Fact]
    public void SeedLastKnownCdnIp_IgnoresInvalidIp()
    {
        CdnService.SeedLastKnownCdnIp("не-ip");
        Assert.NotEqual("не-ip", CdnService.LastKnownCdnIp);

        CdnService.SeedLastKnownCdnIp("138.16.152.133");
        Assert.Equal("138.16.152.133", CdnService.LastKnownCdnIp);
    }
}
