using System;
using System.Security.Cryptography;
using System.Text;

namespace Ven4Tools.Services;

public static class CatalogSignatureVerifier
{
    private const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEYDa6OY3XRYFcy+IH9VJcw85ivmAW
        twludTd3l377NOSUXaKtTPpQAYqWXCcjKfWcrH8wVqV7FvwjqGSwrsZcNQ==
        -----END PUBLIC KEY-----
        """;

    public static bool Verify(string json, string signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(PublicKey);
            return key.VerifyData(
                Encoding.UTF8.GetBytes(json),
                Convert.FromBase64String(signature.Trim()),
                HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }
}
