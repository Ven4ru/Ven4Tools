using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Загрузка и проверка подписи файлового манифеста клиента (client-manifest.json).
/// Манифест — короткий JSON, поэтому качается обычным запросом (как version.json в
/// <see cref="CdnService"/>), без прогресс-бара и цепочки источников FallbackDownloader.
///
/// Fail-closed по подписи и по хосту: манифест задаёт, какие файлы лаунчер положит
/// внутрь папки клиента, поэтому неподписанный, испорченный или пришедший с
/// недоверенного хоста манифест равнозначен его отсутствию — возвращается null,
/// и вызывающий код уходит на обычную полную загрузку архива.
/// </summary>
internal static class ClientManifestFetcher
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static async Task<(ClientFileManifest Manifest, string Json)?> FetchAsync(
        HttpClient client,
        string manifestUrl,
        string signatureUrl,
        CancellationToken cancellationToken)
    {
        if (!DownloadValidator.IsAllowedDownloadHost(manifestUrl) ||
            !DownloadValidator.IsAllowedDownloadHost(signatureUrl))
        {
            return null;
        }

        try
        {
            // Собственный таймаут: клиент загрузок клиента создаётся с бесконечным
            // (см. MainWindow._httpClient), а замолчавший CDN не должен подвешивать
            // обновление — при неудаче есть полный путь загрузки.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(Timeout);

            string json = await client.GetStringAsync(manifestUrl, timeoutCts.Token).ConfigureAwait(false);
            string signature = await client.GetStringAsync(signatureUrl, timeoutCts.Token).ConfigureAwait(false);

            if (!ClientManifestVerifier.Verify(json, signature)) return null;

            var manifest = JsonSerializer.Deserialize<ClientFileManifest>(json);
            if (manifest?.Files == null || manifest.Files.Count == 0) return null;

            return (manifest, json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Отмена пользователем — пробрасываем, чтобы не выглядела как «CDN не отдал
            // манифест» и не запускала следом полную загрузку.
            throw;
        }
        catch
        {
            return null;
        }
    }
}
