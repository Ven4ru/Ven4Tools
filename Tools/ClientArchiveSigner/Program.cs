using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Домен-сепаратор + отдельный ключ, тот же принцип, что у CatalogSigner/
// UpdateManifestSigner/NotificationsSigner. Payload включает version — иначе
// это поле в _ven4tools_signature.json можно подменить без приватного ключа,
// не трогая H_canonical (см. обоснование в плане реализации).
const string DomainSeparator = "Ven4Tools.ClientArchive.v1\n";
const string SignatureEntryName = "_ven4tools_signature.json";

if (args.Length == 3 && args[0] == "verify")
{
    string archivePathV = Path.GetFullPath(args[1]);
    string publicKeyPathV = Path.GetFullPath(args[2]);

    using var zipV = ZipFile.OpenRead(archivePathV);
    var sigEntryV = zipV.GetEntry(SignatureEntryName);
    if (sigEntryV == null)
    {
        Console.Error.WriteLine($"НЕВАЛИДНО: в архиве нет записи {SignatureEntryName}");
        return 1;
    }

    string sigJsonV;
    using (var s = sigEntryV.Open())
    using (var r = new StreamReader(s, Encoding.UTF8))
        sigJsonV = r.ReadToEnd();

    var sigDataV = JsonSerializer.Deserialize<SignatureFile>(sigJsonV);
    if (sigDataV?.sha256_canonical == null || sigDataV.signature == null || sigDataV.version == null)
    {
        Console.Error.WriteLine($"НЕВАЛИДНО: не удалось разобрать {SignatureEntryName}");
        return 1;
    }

    string computedHashV = ComputeCanonicalHashHex(zipV);
    if (!string.Equals(computedHashV, sigDataV.sha256_canonical, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine(
            $"НЕВАЛИДНО: канонический хеш не совпал (посчитан {computedHashV}, в записи {sigDataV.sha256_canonical})");
        return 1;
    }

    using var pubKeyV = ECDsa.Create();
    pubKeyV.ImportFromPem(File.ReadAllText(publicKeyPathV));
    bool validV;
    try
    {
        validV = pubKeyV.VerifyData(
            Encoding.UTF8.GetBytes(DomainSeparator + sigDataV.version + "\n" + sigDataV.sha256_canonical),
            Convert.FromBase64String(sigDataV.signature),
            HashAlgorithmName.SHA256);
    }
    catch { validV = false; }

    if (!validV)
    {
        Console.Error.WriteLine("НЕВАЛИДНО: подпись не соответствует версии/каноническому хешу");
        return 1;
    }
    Console.WriteLine($"OK: архив версии {sigDataV.version} подписан корректно (sha256_canonical={computedHashV})");
    return 0;
}

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  ClientArchiveSigner <archive.zip> <private-key.pem> <version>");
    Console.Error.WriteLine("  ClientArchiveSigner verify <archive.zip> <public-key.pem>");
    return 2;
}

string archivePath = Path.GetFullPath(args[0]);
string privateKeyPath = Path.GetFullPath(args[1]);
string version = args[2];

string canonicalHashHex;
using (var zipRead = ZipFile.OpenRead(archivePath))
    canonicalHashHex = ComputeCanonicalHashHex(zipRead);

using var key = ECDsa.Create();
key.ImportFromPem(File.ReadAllText(privateKeyPath));
byte[] signature = key.SignData(
    Encoding.UTF8.GetBytes(DomainSeparator + version + "\n" + canonicalHashHex),
    HashAlgorithmName.SHA256);

string signatureJson = JsonSerializer.Serialize(new SignatureFile
{
    sha256_canonical = canonicalHashHex,
    signature = Convert.ToBase64String(signature),
    version = version
});

using (var zipWrite = ZipFile.Open(archivePath, ZipArchiveMode.Update))
{
    zipWrite.GetEntry(SignatureEntryName)?.Delete();
    var newEntry = zipWrite.CreateEntry(SignatureEntryName, CompressionLevel.Optimal);
    using var w = new StreamWriter(newEntry.Open(), Encoding.UTF8);
    w.Write(signatureJson);
}

Console.WriteLine($"Подписано: {archivePath} (версия {version}, sha256_canonical={canonicalHashHex})");
return 0;

// ── канонический хеш — ТА ЖЕ логика, что в Ven4Tools.Launcher/Services/
// CanonicalArchiveHasher.cs. Общей библиотеки между Tools/* и лаунчером нет —
// при изменении менять синхронно в обоих местах, иначе уже подписанные
// архивы перестанут проходить проверку в LocalArchiveVerifier.
static string ComputeCanonicalHashHex(ZipArchive archive)
{
    var entries = new List<ZipArchiveEntry>();
    foreach (var e in archive.Entries)
    {
        if (string.IsNullOrEmpty(e.Name)) continue;
        if (string.Equals(e.FullName, SignatureEntryName, StringComparison.Ordinal)) continue;
        entries.Add(e);
    }
    entries.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));

    using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    Span<byte> lenBuf = stackalloc byte[8];
    foreach (var entry in entries)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(entry.FullName);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, (uint)nameBytes.Length);
        incremental.AppendData(lenBuf[..4]);
        incremental.AppendData(nameBytes);

        using var entryStream = entry.Open();
        using var buffered = new MemoryStream();
        entryStream.CopyTo(buffered);
        byte[] content = buffered.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(lenBuf, (ulong)content.LongLength);
        incremental.AppendData(lenBuf);
        incremental.AppendData(content);
    }
    return Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
}

class SignatureFile
{
    public string? sha256_canonical { get; set; }
    public string? signature { get; set; }
    public string? version { get; set; }
}
