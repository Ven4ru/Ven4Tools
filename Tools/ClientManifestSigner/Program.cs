using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

// Инструмент файлового манифеста клиента (client-manifest.json) — списка
// «относительный путь → SHA256 → размер» для каждой опубликованной сборки.
// На его основе лаунчер делает блочное (дельта-) обновление: качает только
// те файлы публикации, которые реально изменились, вместо всего zip-архива.
//
// Domain separation: та же ECDSA-схема, что у UpdateManifestSigner, но с
// ДРУГИМ префиксом подписываемых байтов и ОТДЕЛЬНЫМ ключом. Это разные типы
// манифеста: version.json описывает архив релиза целиком, client-manifest.json —
// его содержимое пофайлово. Подпись одного не должна приниматься за подпись
// другого даже при компрометации одного из ключей.
const string DomainSeparator = "Ven4Tools.ClientManifest.v1\n";

// UTF-8 без BOM: подпись покрывает байты файла, а BOM в начале манифеста
// изменил бы строку, которую лаунчер получает из HTTP-ответа.
var Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

// generate: обход папки публикации → JSON-манифест. Пути относительные, всегда
// через '/' (не зависят от разделителя ОС), отсортированы по алфавиту —
// стабильный порядок даёт читаемый diff манифеста между релизами.
if (args.Length == 4 && args[0] == "generate")
{
    var publishRoot = Path.GetFullPath(args[1]);
    var version = args[2];
    var outputPath = Path.GetFullPath(args[3]);

    if (!Directory.Exists(publishRoot))
    {
        Console.Error.WriteLine($"Не найдена папка публикации: {publishRoot}");
        return 1;
    }

    var entries = new List<ManifestEntry>();
    foreach (var file in Directory.EnumerateFiles(publishRoot, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(publishRoot, file).Replace('\\', '/');
        using var stream = File.OpenRead(file);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        entries.Add(new ManifestEntry(relative, hash, new FileInfo(file).Length));
    }

    entries.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));

    var manifest = new Manifest(
        version,
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        entries);

    var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    File.WriteAllText(outputPath, json, Utf8NoBom);
    Console.WriteLine($"{outputPath} — файлов: {entries.Count}, суммарно байт: {entries.Sum(e => e.Size)}");
    return 0;
}

// genkey: разовая генерация пары ключей подписи файлового манифеста.
// Приватный ключ НИКОГДА не попадает в репозиторий — он хранится только у
// автора проекта (по прецеденту update-manifest-signing-private.pem).
if (args.Length == 2 && args[0] == "genkey")
{
    var prefix = Path.GetFullPath(args[1]);
    using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(prefix + "-private.pem", newKey.ExportPkcs8PrivateKeyPem() + Environment.NewLine, Utf8NoBom);
    File.WriteAllText(prefix + "-public.pem", newKey.ExportSubjectPublicKeyInfoPem() + Environment.NewLine, Utf8NoBom);
    Console.WriteLine(prefix + "-private.pem");
    Console.WriteLine(prefix + "-public.pem");
    return 0;
}

// verify: та же проверка, что и в лаунчере (ClientManifestVerifier), но как
// отдельный CLI-шаг деплой-скрипта — расхождение манифеста и подписи ловится
// до заливки на CDN, а не пользователем (см. инцидент с version.json.sig
// 2026-07-17, из-за которого verify-режим появился и у UpdateManifestSigner).
if (args.Length == 4 && args[0] == "verify")
{
    var manifestPathV = Path.GetFullPath(args[1]);
    var sigPathV = Path.GetFullPath(args[2]);
    var publicKeyPathV = Path.GetFullPath(args[3]);

    var jsonV = File.ReadAllText(manifestPathV, Encoding.UTF8);
    var signatureV = File.ReadAllText(sigPathV, Encoding.UTF8).Trim();

    using var pubKey = ECDsa.Create();
    pubKey.ImportFromPem(File.ReadAllText(publicKeyPathV));

    bool valid;
    try
    {
        valid = pubKey.VerifyData(
            Encoding.UTF8.GetBytes(DomainSeparator + jsonV),
            Convert.FromBase64String(signatureV),
            HashAlgorithmName.SHA256);
    }
    catch
    {
        valid = false;
    }

    if (!valid)
    {
        Console.Error.WriteLine($"НЕВАЛИДНО: {sigPathV} не соответствует {manifestPathV}");
        return 1;
    }
    Console.WriteLine("OK: подпись соответствует файловому манифесту клиента");
    return 0;
}

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  ClientManifestSigner generate <папка-публикации> <версия> <client-manifest.json>");
    Console.Error.WriteLine("  ClientManifestSigner <client-manifest.json> <private-key.pem>");
    Console.Error.WriteLine("  ClientManifestSigner verify <client-manifest.json> <client-manifest.json.sig> <public-key.pem>");
    Console.Error.WriteLine("  ClientManifestSigner genkey <префикс-пути>");
    return 2;
}

var manifestPath = Path.GetFullPath(args[0]);
var manifestJson = File.ReadAllText(manifestPath, Encoding.UTF8);
using var key = ECDsa.Create();
key.ImportFromPem(File.ReadAllText(args[1]));
var signature = key.SignData(Encoding.UTF8.GetBytes(DomainSeparator + manifestJson), HashAlgorithmName.SHA256);
File.WriteAllText(manifestPath + ".sig", Convert.ToBase64String(signature) + Environment.NewLine, Utf8NoBom);
Console.WriteLine(manifestPath + ".sig");
return 0;

// Имена свойств JSON заданы явно (camelCase) — они часть формата, который
// разбирает лаунчер (Ven4Tools.Launcher/Models/ClientFileManifest.cs).
internal sealed record Manifest(
    [property: System.Text.Json.Serialization.JsonPropertyName("version")] string Version,
    [property: System.Text.Json.Serialization.JsonPropertyName("generatedAt")] string GeneratedAt,
    [property: System.Text.Json.Serialization.JsonPropertyName("files")] IReadOnlyList<ManifestEntry> Files);

internal sealed record ManifestEntry(
    [property: System.Text.Json.Serialization.JsonPropertyName("path")] string Path,
    [property: System.Text.Json.Serialization.JsonPropertyName("sha256")] string Sha256,
    [property: System.Text.Json.Serialization.JsonPropertyName("size")] long Size);
