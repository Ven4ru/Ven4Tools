namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Проверка офлайн-подписи архива клиента (Ven4Tools.ClientArchive.v1).
/// Публичный ключ ниже сгенерирован при внедрении фичи локальной установки
/// (2026-08-02). Приватный ключ никогда не публикуется, хранится вне репозитория на машине
/// разработчика (Tools/sign-client-archive.ps1).
/// </summary>
internal static class ClientArchiveVerifier
{
    private const string DomainSeparator = "Ven4Tools.ClientArchive.v1\n";

    private const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEjCIqc8IwYjfg9vtPwGWsWDGLCkCO
        Dyagbvj3XNooWux13HDZyQ7AFA4psns4aVwv4IhlBAh8w/oFn5MutTPV7w==
        -----END PUBLIC KEY-----
        """;

    public static bool Verify(string version, string canonicalHashHex, string? signature) =>
        EcdsaManifestVerifier.Verify(
            PublicKey, DomainSeparator, version + "\n" + canonicalHashHex, signature);
}
