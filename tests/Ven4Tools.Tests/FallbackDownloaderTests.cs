using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class FallbackDownloaderTests
{
    private const string PrimaryUrl = "https://cdn.ven4tools.ru/client.zip";
    private const string FallbackUrl = "https://github.com/Ven4ru/Ven4Tools/client.zip";

    [Fact]
    public async Task DownloadAsync_UsesFallbackAfterPrimaryFailure()
    {
        using var area = new TemporaryDirectory();
        var requests = new List<Uri>();
        using var http = new HttpClient(new DelegateHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return request.RequestUri!.Host == "cdn.ven4tools.ru"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Response(request.RequestUri, "fallback payload");
        }));
        bool switched = false;

        bool usedFallback = await new FallbackDownloader(http).DownloadAsync(
            PrimaryUrl,
            FallbackUrl,
            Path.Combine(area.Path, "client.zip"),
            CancellationToken.None,
            switchingToFallback: () => switched = true);

        Assert.True(usedFallback);
        Assert.True(switched);
        Assert.Equal(new[] { new Uri(PrimaryUrl), new Uri(FallbackUrl) }, requests);
        Assert.Equal(
            "fallback payload",
            await File.ReadAllTextAsync(Path.Combine(area.Path, "client.zip")));
    }

    [Fact]
    public async Task DownloadAsync_DoesNotUseFallbackAfterCancellation()
    {
        using var area = new TemporaryDirectory();
        int requestCount = 0;
        using var http = new HttpClient(new DelegateHandler(request =>
        {
            requestCount++;
            return Response(request.RequestUri, "payload");
        }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        string target = Path.Combine(area.Path, "client.zip");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new FallbackDownloader(http).DownloadAsync(
                PrimaryUrl,
                FallbackUrl,
                target,
                cancellation.Token));

        Assert.Equal(0, requestCount);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".partial"));
    }

    [Fact]
    public async Task DownloadAsync_CleansPartialWhenCancelledDuringTransfer()
    {
        using var area = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new DelegateHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, request.RequestUri),
                Content = new StreamContent(new CancellingStream(cancellation))
            };
            return response;
        }));
        string target = Path.Combine(area.Path, "client.zip");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new FallbackDownloader(http).DownloadAsync(
                PrimaryUrl,
                FallbackUrl,
                target,
                cancellation.Token));

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".partial"));
    }

    [Fact]
    public async Task DownloadAsync_UsesFallbackWhenPrimaryHashIsWrong()
    {
        using var area = new TemporaryDirectory();
        const string expectedBody = "verified fallback";
        string expectedHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(expectedBody)));
        using var http = new HttpClient(new DelegateHandler(request =>
            request.RequestUri!.Host == "cdn.ven4tools.ru"
                ? Response(request.RequestUri, "tampered primary")
                : Response(request.RequestUri, expectedBody)));
        string target = Path.Combine(area.Path, "client.zip");

        bool usedFallback = await new FallbackDownloader(http).DownloadAsync(
            PrimaryUrl,
            FallbackUrl,
            target,
            CancellationToken.None,
            expectedHash);

        Assert.True(usedFallback);
        Assert.Equal(expectedBody, await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(target + ".partial"));
    }

    [Fact]
    public async Task DownloadAsync_RejectsUntrustedRedirectAndCleansPartial()
    {
        using var area = new TemporaryDirectory();
        using var http = new HttpClient(new DelegateHandler(
            _ => Response(new Uri("https://attacker.example/client.zip"), "payload")));
        string target = Path.Combine(area.Path, "client.zip");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FallbackDownloader(http).DownloadAsync(
                PrimaryUrl,
                fallbackUrl: null,
                target,
                CancellationToken.None));

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".partial"));
    }

    [Fact]
    public async Task DownloadAsync_CleansPartialWhenBothSourcesFail()
    {
        using var area = new TemporaryDirectory();
        using var http = new HttpClient(new DelegateHandler(
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway)));
        string target = Path.Combine(area.Path, "client.zip");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => new FallbackDownloader(http).DownloadAsync(
                PrimaryUrl,
                FallbackUrl,
                target,
                CancellationToken.None));

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".partial"));
    }

    private static HttpResponseMessage Response(Uri? finalUri, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUri),
            Content = new ByteArrayContent(bytes)
        };
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_response(request));
        }
    }

    private sealed class CancellingStream : MemoryStream
    {
        private readonly CancellationTokenSource _cancellation;
        private bool _firstRead = true;

        public CancellingStream(CancellationTokenSource cancellation)
            : base(Encoding.UTF8.GetBytes(new string('x', 100_000)))
        {
            _cancellation = cancellation;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_firstRead)
            {
                _cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            _firstRead = false;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
