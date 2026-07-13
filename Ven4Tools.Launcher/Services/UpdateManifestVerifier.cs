using System;
using System.Security.Cryptography;
using System.Text;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Проверка ECDSA-подписи version.json (манифест обновлений клиента и
/// лаунчера, отдаётся CDN). Отдельный ключ от подписи каталога
/// (Ven4Tools/Services/CatalogSignatureVerifier.cs) — компрометация одного
/// не даёт подделать другой. Приватный ключ никогда не лежит на сервере,
/// подпись создаётся офлайн (Tools/UpdateManifestSigner) и заливается на
/// CDN рядом с version.json как version.json.sig.
///
/// Без этой проверки version.json был единственным источником и для URL
/// загрузки, и для его же SHA256 — компрометация CDN означала бы, что
/// оба контроля целостности проходят зелёным одновременно (HIGH-находка
/// аудита 2026-07-13). Подпись даёт независимый от CDN корень доверия.
/// </summary>
internal static class UpdateManifestVerifier
{
    // Domain separation — тот же подписываемый префикс должен использовать
    // Tools/UpdateManifestSigner. Отдельно от подписи каталога, чтобы подпись
    // одного типа манифеста не могла быть спутана с другим.
    private const string DomainSeparator = "Ven4Tools.UpdateManifest.v1\n";

    private const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEt0vWMRNJZnDnb2xPqRdBWerr7bql
        LSxdtvngwUE1R7MqX1BjRv6mv8Fg465l5RQHV5IUWu5a3F/QOaQlnXmS2g==
        -----END PUBLIC KEY-----
        """;

    public static bool Verify(string json, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(PublicKey);
            return key.VerifyData(
                Encoding.UTF8.GetBytes(DomainSeparator + json),
                Convert.FromBase64String(signature.Trim()),
                HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }
}
