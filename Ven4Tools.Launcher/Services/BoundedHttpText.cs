using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Скачивание короткого текстового ответа (version.json/client-manifest.json + их
/// .sig) с жёстким пределом размера, ДО его буферизации в память целиком.
///
/// Клиентский аналог — CatalogLoaderService.TryDownloadAsync (Ven4Tools/Services):
/// там же обоснование — обычный GetStringAsync ничем не ограничен и буферизует весь
/// ответ ещё ДО проверки подписи, так что скомпрометированный или MITM-источник может
/// устроить OOM раньше, чем подпись успеет отклонить подделку. В лаунчере тот же класс
/// вызовов (CdnService/ClientManifestFetcher) использовал обычный GetStringAsync без
/// этого предела — не было применено сюда при появлении лимита в клиенте.
/// </summary>
internal static class BoundedHttpText
{
    // Version.json/client-manifest.json — единицы-десятки КБ; лимит с большим
    // запасом, но конечный.
    private const long MaxResponseBytes = 4 * 1024 * 1024; // 4 МБ

    public static async Task<string> GetStringAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is { } declared && declared > MaxResponseBytes)
            throw new InvalidOperationException($"Ответ {url} превышает допустимый размер ({declared} байт)");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        long total = 0;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxResponseBytes)
                throw new InvalidOperationException($"Ответ {url} превышает допустимый размер (>{MaxResponseBytes} байт)");
            await ms.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
