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
        return await ComputeSha256Async(stream, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Хеш содержимого УЖЕ ОТКРЫТОГО потока — с текущей позиции до конца.
    /// Нужен там, где файл нельзя закрывать между проверкой и использованием:
    /// хендл, из которого посчитан хеш, и есть защита от подмены файла между
    /// этими двумя моментами (TOCTOU). Позицию вызывающий код выставляет сам.
    /// </summary>
    public static async Task<string> ComputeSha256Async(Stream stream, CancellationToken token)
    {
        byte[] hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
