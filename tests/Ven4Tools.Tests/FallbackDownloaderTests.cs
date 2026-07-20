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

    private static IReadOnlyList<DownloadCandidate> Two(HttpClient http) => new[]
    {
        new DownloadCandidate(PrimaryUrl, http, "CDN"),
        new DownloadCandidate(FallbackUrl, http, "GitHub"),
    };

    private static IReadOnlyList<DownloadCandidate> One(HttpClient http) => new[]
    {
        new DownloadCandidate(PrimaryUrl, http, "CDN"),
    };

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
        string? switchedTo = null;

        string usedSource = await new FallbackDownloader().DownloadAsync(
            Two(http),
            Path.Combine(area.Path, "client.zip"),
            CancellationToken.None,
            switchingTo: label => switchedTo = label);

        Assert.Equal("GitHub", usedSource);
        Assert.Equal("GitHub", switchedTo);
        Assert.Equal(new[] { new Uri(PrimaryUrl), new Uri(FallbackUrl) }, requests);
        Assert.Equal(
            "fallback payload",
            await File.ReadAllTextAsync(Path.Combine(area.Path, "client.zip")));
    }

    [Fact]
    public async Task DownloadAsync_ReturnsPrimaryLabelWhenPrimarySucceeds()
    {
        using var area = new TemporaryDirectory();
        bool switched = false;
        using var http = new HttpClient(new DelegateHandler(
            request => Response(request.RequestUri, "primary payload")));

        string usedSource = await new FallbackDownloader().DownloadAsync(
            Two(http),
            Path.Combine(area.Path, "client.zip"),
            CancellationToken.None,
            switchingTo: _ => switched = true);

        Assert.Equal("CDN", usedSource);
        Assert.False(switched); // основной сработал — переключения не было
    }

    [Fact]
    public async Task DownloadAsync_ThrowsWhenPrimaryHostUntrusted()
    {
        using var area = new TemporaryDirectory();
        using var http = new HttpClient(new DelegateHandler(
            request => Response(request.RequestUri, "payload")));
        var candidates = new[]
        {
            new DownloadCandidate("https://evil.example/client.zip", http, "Злой"),
            new DownloadCandidate(FallbackUrl, http, "GitHub"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FallbackDownloader().DownloadAsync(
                candidates,
                Path.Combine(area.Path, "client.zip"),
                CancellationToken.None));
    }

    [Fact]
    public async Task DownloadAsync_SkipsUntrustedNonFirstCandidate()
    {
        using var area = new TemporaryDirectory();
        var requests = new List<Uri>();
        using var http = new HttpClient(new DelegateHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return request.RequestUri!.Host == "cdn.ven4tools.ru"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Response(request.RequestUri, "github payload");
        }));
        var candidates = new[]
        {
            new DownloadCandidate(PrimaryUrl, http, "CDN"),
            new DownloadCandidate("https://evil.example/client.zip", http, "Злой"),
            new DownloadCandidate(FallbackUrl, http, "GitHub"),
        };

        string usedSource = await new FallbackDownloader().DownloadAsync(
            candidates,
            Path.Combine(area.Path, "client.zip"),
            CancellationToken.None);

        Assert.Equal("GitHub", usedSource);
        // Недоверенный резерв не запрашивался — только CDN (503) и GitHub.
        Assert.Equal(new[] { new Uri(PrimaryUrl), new Uri(FallbackUrl) }, requests);
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
            () => new FallbackDownloader().DownloadAsync(
                Two(http),
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
            () => new FallbackDownloader().DownloadAsync(
                Two(http),
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

        string usedSource = await new FallbackDownloader().DownloadAsync(
            Two(http),
            target,
            CancellationToken.None,
            expectedHash);

        Assert.Equal("GitHub", usedSource);
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
            () => new FallbackDownloader().DownloadAsync(
                One(http),
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
            () => new FallbackDownloader().DownloadAsync(
                Two(http),
                target,
                CancellationToken.None));

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".partial"));
    }

    [Fact]
    public async Task DownloadAsync_ThrowsWhenNoCandidates()
    {
        using var area = new TemporaryDirectory();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FallbackDownloader().DownloadAsync(
                System.Array.Empty<DownloadCandidate>(),
                Path.Combine(area.Path, "client.zip"),
                CancellationToken.None));
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
