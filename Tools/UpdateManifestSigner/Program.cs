using System.Security.Cryptography;
using System.Text;

// Domain separation: та же ECDSA-схема, что и у CatalogSigner, но с другим
// префиксом перед подписываемыми байтами и ОТДЕЛЬНЫМ ключом (см.
// UpdateManifestVerifier в лаунчере). Без этого подпись, выпущенная для
// одного типа манифеста, теоретически могла бы быть спутана с другим —
// домен-префикс делает такую путаницу невозможной независимо от ключа.
const string DomainSeparator = "Ven4Tools.UpdateManifest.v1\n";

// verify-режим: та же проверка, что и в лаунчере (UpdateManifestVerifier),
// но как отдельный CLI-шаг deploy-скрипта. Добавлен после инцидента
// 2026-07-17, когда version.json.sig на CDN разошёлся с version.json и
// пользователи ловили "Целостность не подтверждена" — деплой считался
// успешным, потому что раньше ничего не сверяло подпись с уже залитым
// файлом. Теперь deploy-version-manifest.ps1 гоняет verify и до, и после
// заливки на CDN, так что расхождение обнаруживается сразу, а не когда
// об этом сообщит пользователь.
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
    catch (Exception ex)
    {
        // Результат остаётся «невалидно», но причина нужна: битый base64 подписи
        // и неподходящий ключ иначе неотличимы от честного несовпадения.
        Console.Error.WriteLine($"Ошибка проверки подписи: {ex.GetType().Name}: {ex.Message}");
        valid = false;
    }

    if (!valid)
    {
        Console.Error.WriteLine($"НЕВАЛИДНО: {sigPathV} не соответствует {manifestPathV}");
        return 1;
    }
    Console.WriteLine("OK: подпись соответствует манифесту");
    return 0;
}

// edit-режим: правка полей, которые лаунчер проверяет при каждом получении
// манифеста, — до подписи (подпись ставится на текст файла целиком, поэтому любое
// новое поле входит в неё само). Ключ подписи здесь не нужен.
//   --seq auto|<число>         manifest_seq — номер выпуска, защита от повтора старого
//                              манифеста. auto = max(текущий + 1, время Unix в секундах).
//   --revoke-version <версия>  добавить в revokedClientVersions
//   --revoke-canonical <hex>   добавить в revokedClientCanonicalHashes
//   --revoke-file-hash <hex>   добавить в revokedClientHashes
//   --revoke-archive <zip>     отозвать конкретный архив: SHA256 файла → revokedClientHashes,
//                              sha256_canonical из его _ven4tools_signature.json →
//                              revokedClientCanonicalHashes (версию не отзывает).
if (args.Length >= 2 && args[0] == "edit")
{
    try
    {
        return ManifestEditor.Run(args[1], args[2..]);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Ошибка правки манифеста: {ex.Message}");
        return 1;
    }
}

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  UpdateManifestSigner <version.json> <private-key.pem>");
    Console.Error.WriteLine("  UpdateManifestSigner verify <version.json> <version.json.sig> <public-key.pem>");
    Console.Error.WriteLine("  UpdateManifestSigner edit <version.json> [--seq auto|N] [--revoke-version V]");
    Console.Error.WriteLine("        [--revoke-canonical HEX] [--revoke-file-hash HEX] [--revoke-archive ZIP]");
    return 2;
}

var manifestPath = Path.GetFullPath(args[0]);
var json = File.ReadAllText(manifestPath, Encoding.UTF8);
using var key = ECDsa.Create();
key.ImportFromPem(File.ReadAllText(args[1]));
var signature = key.SignData(Encoding.UTF8.GetBytes(DomainSeparator + json), HashAlgorithmName.SHA256);
File.WriteAllText(manifestPath + ".sig", Convert.ToBase64String(signature) + Environment.NewLine);
Console.WriteLine(manifestPath + ".sig");
return 0;
