#nullable enable
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Правка полей version.json перед подписью (режим edit). Имена полей — те же, что в
/// лаунчере (Ven4Tools.Launcher/Models/CdnVersionInfo.cs); общей сборки между Tools/*
/// и лаунчером нет, поэтому при переименовании менять синхронно.
/// </summary>
internal static class ManifestEditor
{
    internal const string SeqField = "manifest_seq";
    internal const string RevokedVersionsField = "revokedClientVersions";
    internal const string RevokedCanonicalField = "revokedClientCanonicalHashes";
    internal const string RevokedFileHashesField = "revokedClientHashes";

    private const string SignatureEntryName = "_ven4tools_signature.json";

    public static int Run(string manifestPath, string[] options)
    {
        manifestPath = Path.GetFullPath(manifestPath);
        var root = JsonNode.Parse(File.ReadAllText(manifestPath, Encoding.UTF8)) as JsonObject
            ?? throw new InvalidDataException("version.json — не JSON-объект");

        for (int i = 0; i < options.Length; i++)
        {
            string option = options[i];
            string value = i + 1 < options.Length
                ? options[++i]
                : throw new ArgumentException($"у параметра {option} нет значения");

            switch (option)
            {
                case "--seq":
                    long seq = value == "auto" ? NextSeq(root) : long.Parse(value);
                    long? current = ReadSeq(root);
                    if (current != null && seq <= current)
                        throw new ArgumentException($"новый {SeqField} {seq} не больше текущего {current}");
                    root[SeqField] = seq;
                    Console.WriteLine($"{SeqField} = {seq}");
                    break;
                case "--revoke-version":
                    AddUnique(root, RevokedVersionsField, value.Trim());
                    break;
                case "--revoke-canonical":
                    AddUnique(root, RevokedCanonicalField, RequireSha256(value));
                    break;
                case "--revoke-file-hash":
                    AddUnique(root, RevokedFileHashesField, RequireSha256(value));
                    break;
                case "--revoke-archive":
                    RevokeArchive(root, value);
                    break;
                default:
                    throw new ArgumentException($"неизвестный параметр {option}");
            }
        }

        // UTF-8 без BOM: BOM ломал проверку подписи в лаунчере (см. BoundedHttpText).
        string json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        File.WriteAllText(manifestPath, json + Environment.NewLine, new UTF8Encoding(false));
        Console.WriteLine($"Сохранено: {manifestPath}");
        return 0;
    }

    internal static long NextSeq(JsonObject root)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long? current = ReadSeq(root);
        return current == null ? now : Math.Max(current.Value + 1, now);
    }

    private static long? ReadSeq(JsonObject root) =>
        root[SeqField] is JsonValue v && v.TryGetValue(out long seq) ? seq : null;

    private static void RevokeArchive(JsonObject root, string archivePath)
    {
        archivePath = Path.GetFullPath(archivePath);
        string fileHash;
        using (var stream = File.OpenRead(archivePath))
            fileHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        AddUnique(root, RevokedFileHashesField, fileHash);

        using var zip = ZipFile.OpenRead(archivePath);
        var entry = zip.GetEntry(SignatureEntryName);
        if (entry == null)
        {
            Console.WriteLine($"В архиве нет {SignatureEntryName} — отозван только SHA256 файла {fileHash}");
            return;
        }
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        var signature = JsonNode.Parse(reader.ReadToEnd());
        string? canonical = signature?["sha256_canonical"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(canonical))
            throw new InvalidDataException($"в {SignatureEntryName} нет sha256_canonical");
        AddUnique(root, RevokedCanonicalField, RequireSha256(canonical));
        Console.WriteLine($"Архив версии {signature?["version"]?.GetValue<string>()} отозван: файл {fileHash}, содержимое {canonical.ToLowerInvariant()}");
    }

    private static string RequireSha256(string value)
    {
        string hex = value.Trim().ToLowerInvariant();
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit))
            throw new ArgumentException($"не SHA256 (64 шестнадцатеричных символа): {value}");
        return hex;
    }

    private static void AddUnique(JsonObject root, string field, string value)
    {
        if (root[field] is not JsonArray list)
        {
            list = new JsonArray();
            root[field] = list;
        }
        bool exists = list.Any(n => n is JsonValue v && v.TryGetValue(out string? s) &&
            string.Equals(s?.Trim(), value, StringComparison.OrdinalIgnoreCase));
        if (exists)
        {
            Console.WriteLine($"{field}: «{value}» уже в списке");
            return;
        }
        list.Add(value);
        Console.WriteLine($"{field}: добавлено «{value}»");
    }
}
