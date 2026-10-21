using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Детектор «зависшей» загрузки и докачка с того же байта (лаунчер 3.12):
/// разбор ответа на Range, переключение источника по отсутствию данных, медленный
/// живой канал, выключение флагом манифеста.
/// </summary>
public sealed class LauncherStallResumeTests
{
    private const string PrimaryUrl = "https://cdn.ven4tools.ru/client.zip";
    private const string FallbackUrl = "https://github.com/Ven4ru/Ven4Tools/client.zip";

    private static readonly byte[] Payload = Encoding.UTF8.GetBytes(new string('a', 4000) + new string('b', 4000));
    private static readonly string PayloadSha = Convert.ToHexString(SHA256.HashData(Payload));

    // --- DownloadResume.Classify / ExpectedTotal ---

    [Fact]
    public void Classify_206FromRequestedByte_Appends()
    {
        var range = new ContentRangeHeaderValue(100, 199, 200);
        Assert.Equal(ResumeAction.Append, DownloadResume.Classify(HttpStatusCode.PartialContent, range, 100));
    }

    [Fact]
    public void Classify_200_RestartsFromZero()
    {
        Assert.Equal(ResumeAction.RestartFromZero, DownloadResume.Classify(HttpStatusCode.OK, null, 100));
    }

    [Theory]
    [InlineData(50L)]   // не с того байта
    [InlineData(150L)]
    public void Classify_206FromWrongByte_IsRejected(long from)
    {
        var range = new ContentRangeHeaderValue(from, 199, 200);
        Assert.Equal(ResumeAction.Reject, DownloadResume.Classify(HttpStatusCode.PartialContent, range, 100));
    }

    [Fact]
    public void Classify_206WithoutContentRange_IsRejected()
    {
        Assert.Equal(ResumeAction.Reject, DownloadResume.Classify(HttpStatusCode.PartialContent, null, 100));
    }

    [Fact]
    public void Classify_416_IsRejected()
    {
        Assert.Equal(ResumeAction.Reject,
            DownloadResume.Classify(HttpStatusCode.RequestedRangeNotSatisfiable, new ContentRangeHeaderValue(200), 100));
    }

    [Fact]
    public void Classify_LengthNotBeyondResumePoint_IsRejected()
    {
        // «bytes 100-…/100»: файл не длиннее уже скачанного — докачивать нечего.
        var range = new ContentRangeHeaderValue(100, 100, 100);
        Assert.Equal(ResumeAction.Reject, DownloadResume.Classify(HttpStatusCode.PartialContent, range, 100));
    }

    [Fact]
    public void ExpectedTotal_UsesContentRangeLength_ThenContentLength()
    {
        Assert.Equal(200, DownloadResume.ExpectedTotal(ResumeAction.Append, new ContentRangeHeaderValue(100, 199, 200), 100, 100));
        Assert.Equal(250, DownloadResume.ExpectedTotal(ResumeAction.Append, new ContentRangeHeaderValue(100, 199), 150, 100));
        Assert.Null(DownloadResume.ExpectedTotal(ResumeAction.Append, null, null, 100));
        Assert.Equal(300, DownloadResume.ExpectedTotal(ResumeAction.RestartFromZero, null, 300, 100));
    }

    [Fact]
    public void IsResumableFailure_ExcludesIntegrityFailures()
    {
        Assert.True(FallbackDownloader.IsResumableFailure(new DownloadStalledException("x")));
        Assert.True(FallbackDownloader.IsResumableFailure(new IOException("обрыв")));
        Assert.True(FallbackDownloader.IsResumableFailure(new HttpRequestException("503")));
        Assert.False(FallbackDownloader.IsResumableFailure(new IntegrityCheckFailedException("хеш")));
        Assert.False(FallbackDownloader.IsResumableFailure(new InvalidOperationException("редирект")));
    }

    // --- Загрузчик целиком (фальшивый транспорт, пороги в миллисекундах) ---

    [Fact]
    public async Task StalledSource_SwitchesAndResumesFromSameByte()
    {
        using var area = new TemporaryDirectory();
        var requests = new List<(string Host, long? From)>();
        using var http = new HttpClient(new DelegateHandler(request =>
        {
            long? from = request.Headers.Range?.Ranges.First().From;
            requests.Add((request.RequestUri!.Host, from));
            if (request.RequestUri.Host == "cdn.ven4tools.ru")
            {
                // Отдаёт первые 1000 байт и замолкает, не закрывая соединение.
                return Ok(request.RequestUri, new StallingStream(Payload.AsSpan(0, 1000).ToArray()), Payload.Length);
            }
            return Partial(request.RequestUri, from ?? 0);
        }));
        string? reason = null;

        using var result = await new FallbackDownloader(true, TimeSpan.FromMilliseconds(300)).DownloadAsync(
            Two(http), Path.Combine(area.Path, "client.zip"), CancellationToken.None, PayloadSha,
            switchingTo: (_, r) => reason = r);

        Assert.Equal("GitHub", result.SourceLabel);
        Assert.Equal("перестал передавать данные", reason);
        Assert.Equal(new[] { ("cdn.ven4tools.ru", (long?)null), ("github.com", (long?)1000) }, requests);
        result.Dispose();
        Assert.Equal(Payload, await File.ReadAllBytesAsync(Path.Combine(area.Path, "client.zip")));
    }

    [Fact]
    public async Task ResumeAnswered200_RestartsFromZeroWithoutCorruption()
    {
        using var area = new TemporaryDirectory();
        using var http = new HttpClient(new DelegateHandler(request =>
            request.RequestUri!.Host == "cdn.ven4tools.ru"
                ? Ok(request.RequestUri, new StallingStream(Payload.AsSpan(0, 1000).ToArray()), Payload.Length)
                // Источник игнорирует Range и отдаёт файл целиком.
                : Ok(request.RequestUri, new MemoryStream(Payload), Payload.Length)));

        using var result = await new FallbackDownloader(true, TimeSpan.FromMilliseconds(300)).DownloadAsync(
            Two(http), Path.Combine(area.Path, "client.zip"), CancellationToken.None, PayloadSha);
        result.Dispose();

        Assert.Equal(Payload, await File.ReadAllBytesAsync(Path.Combine(area.Path, "client.zip")));
    }

    [Fact]
    public async Task SlowButAliveSource_IsNotTreatedAsStalled()
    {
        // Байты идут каждые 50 мс при пороге 400 мс — источник медленный, но живой:
        // переключаться нельзя, хотя вся загрузка длится дольше порога.
        using var area = new TemporaryDirectory();
        byte[] small = Payload.AsSpan(0, 20).ToArray();
        string smallSha = Convert.ToHexString(SHA256.HashData(small));
        bool switched = false;
        using var http = new HttpClient(new DelegateHandler(request =>
            Ok(request.RequestUri, new TrickleStream(small, TimeSpan.FromMilliseconds(50)), small.Length)));

        using var result = await new FallbackDownloader(true, TimeSpan.FromMilliseconds(400)).DownloadAsync(
            Two(http), Path.Combine(area.Path, "client.zip"), CancellationToken.None, smallSha,
            switchingTo: (_, _) => switched = true);

        Assert.Equal("CDN", result.SourceLabel);
        Assert.False(switched);
    }

    [Fact]
    public async Task DetectorOff_NextSourceStartsFromZero()
    {
        using var area = new TemporaryDirectory();
        var ranges = new List<long?>();
        using var http = new HttpClient(new DelegateHandler(request =>
        {
            ranges.Add(request.Headers.Range?.Ranges.First().From);
            return request.RequestUri!.Host == "cdn.ven4tools.ru"
                ? Ok(request.RequestUri, new StallingStream(Payload.AsSpan(0, 1000).ToArray()), Payload.Length)
                : Ok(request.RequestUri, new MemoryStream(Payload), Payload.Length);
        }));

        using var result = await new FallbackDownloader(false, TimeSpan.FromMilliseconds(300)).DownloadAsync(
            Two(http), Path.Combine(area.Path, "client.zip"), CancellationToken.None, PayloadSha);

        Assert.Equal(new long?[] { null, null }, ranges);
    }

    [Fact]
    public async Task WithoutExpectedHash_NoResume()
    {
        // Без SHA256 склейку кусков проверить нечем — каждый источник с нуля.
        using var area = new TemporaryDirectory();
        var ranges = new List<long?>();
        using var http = new HttpClient(new DelegateHandler(request =>
        {
            ranges.Add(request.Headers.Range?.Ranges.First().From);
            return request.RequestUri!.Host == "cdn.ven4tools.ru"
                ? Ok(request.RequestUri, new StallingStream(Payload.AsSpan(0, 1000).ToArray()), Payload.Length)
                : Ok(request.RequestUri, new MemoryStream(Payload), Payload.Length);
        }));

        using var result = await new FallbackDownloader(true, TimeSpan.FromMilliseconds(300)).DownloadAsync(
            Two(http), Path.Combine(area.Path, "client.zip"), CancellationToken.None, expectedSha256: null);

        Assert.Equal(new long?[] { null, null }, ranges);
    }

    [Fact]
    public async Task EverySourceStalls_FailsAsTimeoutAndCleansPartial()
    {
        using var area = new TemporaryDirectory();
        string target = Path.Combine(area.Path, "client.zip");
        int calls = 0;
        using var http = new HttpClient(new DelegateHandler(request =>
        {
            Interlocked.Increment(ref calls);
            return Ok(request.RequestUri, new StallingStream(Array.Empty<byte>()), Payload.Length);
        }));

        await Assert.ThrowsAnyAsync<TimeoutException>(() => new FallbackDownloader(true, TimeSpan.FromMilliseconds(200))
            .DownloadAsync(Two(http), target, CancellationToken.None, PayloadSha));

        // Круг без единого байта — повторных кругов нет.
        Assert.Equal(2, calls);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".partial"));
    }

    // --- Флаг манифеста ---

    [Fact]
    public void LauncherFlags_DeserializeFromSignedManifestShape()
    {
        var info = JsonSerializer.Deserialize<CdnVersionInfo>("""{ "launcher_flags": { "stall_detection": false } }""");
        Assert.False(info!.LauncherFlags!.StallDetection);

        var empty = JsonSerializer.Deserialize<CdnVersionInfo>("""{ "client": { "version": "6.5.0" } }""");
        Assert.Null(empty!.LauncherFlags);
    }

    // --- Помощники ---

    private static IReadOnlyList<DownloadCandidate> Two(HttpClient http) => new[]
    {
        new DownloadCandidate(PrimaryUrl, http, "CDN"),
        new DownloadCandidate(FallbackUrl, http, "GitHub"),
    };

    private static HttpResponseMessage Ok(Uri? uri, Stream body, long length)
    {
        var content = new StreamContent(body);
        content.Headers.ContentLength = length;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri),
            Content = content,
        };
    }

    private static HttpResponseMessage Partial(Uri? uri, long from)
    {
        var content = new ByteArrayContent(Payload, (int)from, Payload.Length - (int)from);
        content.Headers.ContentRange = new ContentRangeHeaderValue(from, Payload.Length - 1, Payload.Length);
        return new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri),
            Content = content,
        };
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_response(request));
        }
    }

    /// <summary>Отдаёт начало и дальше молчит, пока чтение не отменят.</summary>
    private sealed class StallingStream : MemoryStream
    {
        public StallingStream(byte[] head) : base(head) { }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await base.ReadAsync(buffer, cancellationToken);
            if (read > 0) return read;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    /// <summary>По одному байту с паузой — медленный, но живой канал.</summary>
    private sealed class TrickleStream : MemoryStream
    {
        private readonly TimeSpan _delay;

        public TrickleStream(byte[] data, TimeSpan delay) : base(data) => _delay = delay;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
        }
    }
}
