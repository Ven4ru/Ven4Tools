using System.Security.Cryptography;
using System.Text;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: NotificationsSigner <notifications.json> <private-key.pem>");
    return 2;
}

// Domain separation — своя строка, отдельная от CatalogSigner/UpdateManifestSigner
// (и отдельный ключ, см. NotificationsVerifier в лаунчере). notifications.json
// раздаётся через raw.githubusercontent.com без подписи не отличался бы от
// подделываемого текста при компрометации GitHub-аккаунта — подпись даёт
// независимый от хостинга корень доверия (тот же паттерн, что и version.json).
const string DomainSeparator = "Ven4Tools.Notifications.v1\n";

var manifestPath = Path.GetFullPath(args[0]);
var json = File.ReadAllText(manifestPath, Encoding.UTF8);
using var key = ECDsa.Create();
key.ImportFromPem(File.ReadAllText(args[1]));
var signature = key.SignData(Encoding.UTF8.GetBytes(DomainSeparator + json), HashAlgorithmName.SHA256);
File.WriteAllText(manifestPath + ".sig", Convert.ToBase64String(signature) + Environment.NewLine);
Console.WriteLine(manifestPath + ".sig");
return 0;
