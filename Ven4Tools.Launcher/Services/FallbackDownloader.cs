using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Launcher.Services;

internal sealed class FallbackDownloader
{
    private readonly HttpClient _httpClient;

    public FallbackDownloader(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<bool> DownloadAsync(
        string primaryUrl,
        string? fallbackUrl,
        string targetPath,
        CancellationToken cancellationToken,
        string? expectedSha256 = null,
        Action<long, long?>? progress = null,
        Action? switchingToFallback = null)
    {
        if (!DownloadValidator.IsAllowedDownloadHost(primaryUrl))
        {
            throw new InvalidOperationException("Основной URL загрузки не входит в список доверенных.");
        }

        try
        {
            await DownloadSingleAsync(
                primaryUrl,
                targetPath,
                cancellationToken,
                expectedSha256,
                progress);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Отмена именно вызывающей стороной (наш токен) — не пытаемся зеркало,
            // операция в любом случае должна остановиться.
            throw;
        }
        catch when (
            !string.IsNullOrWhiteSpace(fallbackUrl) &&
            !string.Equals(primaryUrl, fallbackUrl, StringComparison.OrdinalIgnoreCase) &&
            DownloadValidator.IsAllowedDownloadHost(fallbackUrl))
        {
            switchingToFallback?.Invoke();
            await DownloadSingleAsync(
                fallbackUrl!,
                targetPath,
                cancellationToken,
                expectedSha256,
                progress);
            return true;
        }
    }

    private async Task DownloadSingleAsync(
        string url,
        string targetPath,
        CancellationToken cancellationToken,
        string? expectedSha256,
        Action<long, long?>? progress)
    {
        string partialPath = targetPath + ".partial";
        TryDelete(partialPath);

        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            if (!DownloadValidator.IsAllowedDownloadHostAfterRedirect(response))
            {
                throw new InvalidOperationException("Редирект загрузки ведёт на недоверенный домен.");
            }

            long? totalBytes = response.Content.Headers.ContentLength;
            long bytesRead = 0;
            var buffer = new byte[81920];

            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(
                partialPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                int count;
                while ((count = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    bytesRead += count;
                    progress?.Invoke(bytesRead, totalBytes);
                }

                await destination.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (totalBytes is > 0 && bytesRead != totalBytes.Value)
            {
                throw new IOException(
                    $"Загрузка неполная: получено {bytesRead} из {totalBytes.Value} байт.");
            }

            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                string actualSha256 = await ComputeSha256Async(partialPath, cancellationToken);
                if (!string.Equals(
                    actualSha256,
                    expectedSha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Контрольная сумма загруженного файла не совпала.");
                }
            }

            File.Move(partialPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Основная операция сообщит исходную ошибку; очистка не должна её скрывать.
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }
}
