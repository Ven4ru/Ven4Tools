using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Канонический хеш содержимого zip-архива клиента, БЕЗ записи
/// _ven4tools_signature.json — детерминированная величина, над которой
/// ClientArchiveSigner ставит офлайн-подпись внутри самого архива.
/// Алгоритм ЗАФИКСИРОВАН и продублирован байт-в-байт в Tools/ClientArchiveSigner —
/// отдельная от Shared/ пара (та обслуживает только Ven4Tools/Ven4Tools.Launcher,
/// сюда не подключена). Любое изменение здесь делает несовместимыми уже подписанные
/// архивы, менять только синхронно в обоих местах.
/// </summary>
internal static class CanonicalArchiveHasher
{
    internal const string SignatureEntryName = "_ven4tools_signature.json";

    /// <summary>
    /// Порядок: записи (кроме SignatureEntryName и каталогов — пустое Name),
    /// отсортированные по FullName (Ordinal). Для каждой записи в хеш подаётся:
    /// 4-байтовая little-endian длина UTF8-имени + само имя + 8-байтовая
    /// little-endian длина содержимого + само содержимое. Длины-префиксы
    /// исключают неоднозначность конкатенации (иначе записи "ab"+"" и "a"+"b"
    /// с одинаковой суммой байт дали бы одинаковый хеш).
    /// </summary>
    public static string ComputeHex(ZipArchive archive)
    {
        var entries = archive.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name))
            .Where(e => !string.Equals(e.FullName, SignatureEntryName, StringComparison.Ordinal))
            .OrderBy(e => e.FullName, StringComparer.Ordinal)
            .ToList();

        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lenBuf = stackalloc byte[8];
        foreach (var entry in entries)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(entry.FullName);
            BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, (uint)nameBytes.Length);
            incremental.AppendData(lenBuf[..4]);
            incremental.AppendData(nameBytes);

            using var entryStream = entry.Open();
            using var buffered = new MemoryStream();
            entryStream.CopyTo(buffered);
            byte[] content = buffered.ToArray();
            BinaryPrimitives.WriteUInt64LittleEndian(lenBuf, (ulong)content.LongLength);
            incremental.AppendData(lenBuf);
            incremental.AppendData(content);
        }

        return Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
    }
}
