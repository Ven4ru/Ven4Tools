using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Launcher.Helpers;

/// <summary>
/// Единая реализация SHA-256 файла для лаунчера. Раньше была продублирована
/// байт-в-байт в LocalArchiveVerifier и FallbackDownloader — тот же FileStream
/// (FileShare.Read, buffered async) и тот же SHA256.HashDataAsync, только с
/// разным регистром итоговой строки.
/// </summary>
internal static class FileHashHelper
{
    public static async Task<string> ComputeSha256Async(string path, CancellationToken token)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
