using System.Security.Cryptography;
using System.Text;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: UpdateManifestSigner <version.json> <private-key.pem>");
    return 2;
}

// Domain separation: та же ECDSA-схема, что и у CatalogSigner, но с другим
// префиксом перед подписываемыми байтами и ОТДЕЛЬНЫМ ключом (см.
// UpdateManifestVerifier в лаунчере). Без этого подпись, выпущенная для
// одного типа манифеста, теоретически могла бы быть спутана с другим —
// домен-префикс делает такую путаницу невозможной независимо от ключа.
const string DomainSeparator = "Ven4Tools.UpdateManifest.v1\n";

var manifestPath = Path.GetFullPath(args[0]);
var json = File.ReadAllText(manifestPath, Encoding.UTF8);
using var key = ECDsa.Create();
key.ImportFromPem(File.ReadAllText(args[1]));
var signature = key.SignData(Encoding.UTF8.GetBytes(DomainSeparator + json), HashAlgorithmName.SHA256);
File.WriteAllText(manifestPath + ".sig", Convert.ToBase64String(signature) + Environment.NewLine);
Console.WriteLine(manifestPath + ".sig");
return 0;
