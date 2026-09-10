using System.Net;
using System.Net.Http;
using System.Text;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Регрессия 2026-09-10: <see cref="BoundedHttpText"/> заменил
/// <c>HttpClient.GetStringAsync</c> для version.json/client-manifest.json и их
/// подписей, но декодировал байты через <c>Encoding.UTF8.GetString</c> — а тот
/// оставляет BOM (EF BB BF) первым символом строки, в отличие от заменённого метода.
/// Подпись считается по содержимому БЕЗ BOM, поэтому каждый манифест, выложенный с
/// BOM (а именно так его пишет PowerShell-скрипт деплоя), не проходил ECDSA-проверку:
/// лаунчер молча считал CDN недоступным и всегда уходил на GitHub, а дельта-обновление
/// и «Проверить и восстановить клиент» переставали работать вовсе.
/// </summary>
public sealed class BoundedHttpTextTests
{
    private const string Payload = "{\n    \"client\": { \"version\": \"5.1.1\" }\n}";

    private static HttpClient ClientReturning(byte[] body, string contentType = "application/json")
    {
        var handler = new StubHandler(body, contentType);
        return new HttpClient(handler);
    }

    [Fact]
    public async Task GetStringAsync_StripsUtf8Bom()
    {
        byte[] withBom = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(Payload))
            .ToArray();

        using HttpClient client = ClientReturning(withBom);

        string text = await BoundedHttpText.GetStringAsync(client, "https://cdn.example/version.json", default);

        Assert.Equal(Payload, text);
        Assert.DoesNotContain('﻿', text);
    }

    [Fact]
    public async Task GetStringAsync_KeepsContentWithoutBomIntact()
    {
        using HttpClient client = ClientReturning(Encoding.UTF8.GetBytes(Payload));

        Assert.Equal(
            Payload,
            await BoundedHttpText.GetStringAsync(client, "https://cdn.example/version.json", default));
    }

    /// <summary>
    /// Подпись отдаётся как text/plain и тоже может приехать с BOM — она идёт в
    /// Convert.FromBase64String, где лишний символ даёт FormatException, а не
    /// «подпись не совпала».
    /// </summary>
    [Fact]
    public async Task GetStringAsync_StripsBomFromSignatureResponse()
    {
        const string signature = "AGyR7pXF6yO0QlyoQuzgcLa1x9f7+qkmsPoI9OsJYlB3GnSFPHzfiySm3qpzID1EtbpRaSOrma6AF4Wf7uw6hA==";
        byte[] withBom = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(signature))
            .ToArray();

        using HttpClient client = ClientReturning(withBom, "text/plain");

        string text = await BoundedHttpText.GetStringAsync(client, "https://cdn.example/version.json.sig", default);

        Assert.Equal(signature, text);
        _ = Convert.FromBase64String(text); // не должно бросить
    }

    [Fact]
    public async Task GetStringAsync_RejectsResponseOverSizeLimit()
    {
        // 5 МБ при лимите 4 МБ. Заявленный Content-Length обязан отсечь ответ
        // до буферизации — предел размера правкой BOM не должен потеряться.
        using HttpClient client = ClientReturning(new byte[5 * 1024 * 1024]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => BoundedHttpText.GetStringAsync(client, "https://cdn.example/version.json", default));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly string _contentType;

        public StubHandler(byte[] body, string contentType)
        {
            _body = body;
            _contentType = contentType;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(_body);
            // Без charset — ровно как отдаёт nginx CDN: кодировка определяется по BOM.
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
