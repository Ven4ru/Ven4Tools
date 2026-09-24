using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Регрессии ревью ядра лаунчера (Services/Helpers): тайм-аут чтения тела в
/// BoundedHttpText, пробельный ожидаемый хеш в FallbackDownloader и культура
/// метки времени манифеста в ClientManifestBuilder.
/// </summary>
public sealed class LauncherCoreReviewFixesTests
{
    /// <summary>
    /// HttpClient.Timeout при ResponseHeadersRead не ограничивает чтение тела:
    /// сервер, приславший заголовки и замолчавший, подвешивал вызов навсегда.
    /// </summary>
    [Fact]
    public async Task BoundedHttpText_TimesOutWhenBodyStalls()
    {
        using var client = new HttpClient(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream())
        }))
        {
            Timeout = TimeSpan.FromMilliseconds(200)
        };

        // WaitAsync — страховка самого теста: при регрессии он падает по
        // TimeoutException, а не висит до бесконечности.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BoundedHttpText.GetStringAsync(client, "https://cdn.example/version.json", default)
                .WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// Строка из пробелов вместо хеша раньше означала «сверку пропустить», хотя
    /// вызывающий код клиента считал такую целостность подтверждённой.
    /// </summary>
    [Fact]
    public async Task FallbackDownloader_WhitespaceExpectedHashIsNotSkipped()
    {
        using var area = new TemporaryDirectory();
        using var http = new HttpClient(new DelegateHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, request.RequestUri),
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("payload"))
        }));
        string target = Path.Combine(area.Path, "client.zip");

        await Assert.ThrowsAsync<IntegrityCheckFailedException>(
            () => new FallbackDownloader().DownloadAsync(
                new[] { new DownloadCandidate("https://cdn.ven4tools.ru/client.zip", http, "CDN") },
                target,
                CancellationToken.None,
                expectedSha256: "   "));

        Assert.False(File.Exists(target));
    }

    /// <summary>
    /// Пользовательский формат даты без InvariantCulture берёт календарь текущей
    /// культуры: на th-TH год получался буддийским (2569 вместо 2026).
    /// </summary>
    [Fact]
    public async Task ClientManifestBuilder_GeneratedAtIsCultureInvariant()
    {
        using var area = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(area.Path, "Ven4Tools.exe"), "exe");

        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            var manifest = await ClientManifestBuilder.BuildFromDirectoryAsync(area.Path, "5.0.0", CancellationToken.None);

            Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$"), manifest.GeneratedAt);
            Assert.StartsWith(DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture), manifest.GeneratedAt);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_response(request));
        }
    }

    /// <summary>Тело ответа, которое никогда не отдаёт ни байта, пока его не отменят.</summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
