namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Проверка ECDSA-подписи файлового манифеста клиента (client-manifest.json —
/// список «путь → SHA256 → размер» для дельта-обновления).
///
/// Отдельный ключ и отдельный domain separator от <see cref="UpdateManifestVerifier"/>
/// (version.json) — это принципиально: version.json описывает архив релиза целиком,
/// client-manifest.json задаёт, какие ОТДЕЛЬНЫЕ файлы лаунчер скачает и положит
/// внутрь папки установленного клиента. Подделка второго опаснее подделки первого
/// (подменяется одна dll вместо целого архива), поэтому подпись одного типа
/// манифеста не должна приниматься за подпись другого ни при каких условиях —
/// это гарантирует разный подписываемый префикс независимо от ключей.
///
/// Приватный ключ никогда не лежит на сервере: подпись создаётся офлайн
/// (Tools/ClientManifestSigner) и заливается на CDN рядом с манифестом.
/// </summary>
internal static class ClientManifestVerifier
{
    // Domain separation — тот же префикс должен использовать Tools/ClientManifestSigner.
    private const string DomainSeparator = "Ven4Tools.ClientManifest.v1\n";

    // Публичная половина ключа подписи файлового манифеста. Приватная половина —
    // только у автора проекта, вне репозитория (см. Tools/deploy-client-manifest.ps1).
    private const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAERBnxMHOZ5A+sYIm8fRxRaNnF2Jv7
        slm7D5D1RpG7S0fgpsOUgftLcgcy52iVNIht3PtnEcTLm7y9s4z2jHM3tg==
        -----END PUBLIC KEY-----
        """;

    public static bool Verify(string json, string? signature) =>
        EcdsaManifestVerifier.Verify(PublicKey, DomainSeparator, json, signature);
}
