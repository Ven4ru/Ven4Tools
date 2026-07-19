using System;
using System.Security.Cryptography;
using System.Text;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Общая механика ECDSA P-256/SHA-256 проверки подписи с domain separation —
/// используется NotificationsVerifier и UpdateManifestVerifier. Оба класса
/// проверяют один и тот же тип подписи, отличаясь только ключом/доменом —
/// раньше тело Verify было продублировано дословно в обоих.
/// </summary>
internal static class EcdsaManifestVerifier
{
    public static bool Verify(string publicKeyPem, string domainSeparator, string json, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.VerifyData(
                Encoding.UTF8.GetBytes(domainSeparator + json),
                Convert.FromBase64String(signature.Trim()),
                HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }
}
