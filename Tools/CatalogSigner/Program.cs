using System.Security.Cryptography;
using System.Text;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: CatalogSigner <master.json> <private-key.pem>");
    return 2;
}

var catalogPath = Path.GetFullPath(args[0]);
var json = File.ReadAllText(catalogPath, Encoding.UTF8);
using var key = ECDsa.Create();
key.ImportFromPem(File.ReadAllText(args[1]));
var signature = key.SignData(Encoding.UTF8.GetBytes(json), HashAlgorithmName.SHA256);
File.WriteAllText(catalogPath + ".sig", Convert.ToBase64String(signature) + Environment.NewLine);
Console.WriteLine(catalogPath + ".sig");
return 0;
