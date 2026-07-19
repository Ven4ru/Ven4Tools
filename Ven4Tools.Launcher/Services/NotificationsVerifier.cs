namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Проверка ECDSA-подписи notifications.json (текстовые уведомления в лаунчере,
/// раздаётся с raw.githubusercontent.com). Отдельный ключ от подписи version.json
/// и от подписи каталога — компрометация одного не даёт подделать другой.
/// Приватный ключ создаётся и хранится офлайн (Tools/NotificationsSigner),
/// подпись коммитится в репозиторий рядом с notifications.json.sig.
///
/// Без этой проверки текст уведомления полностью определялся содержимым файла
/// на GitHub — компрометация аккаунта/репозитория давала бы возможность показать
/// пользователю произвольный текст (соц-инженерия) без какого-либо независимого
/// контроля. Подпись не защищает от компрометации самого GitHub-аккаунта (тем же
/// аккаунтом можно закоммитить и валидную подпись), но защищает от компрометации
/// ТОЛЬКО хостинга (CDN/raw.githubusercontent.com выдачи) без доступа к приватному
/// ключу, который никогда не покидает офлайн-машину.
/// </summary>
internal static class NotificationsVerifier
{
    private const string DomainSeparator = "Ven4Tools.Notifications.v1\n";

    private const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE6Ti7KvcRZvAkykjWDhWZMMbjJtJE
        nRee5CbwG3GOb1JdPu8/2sjTfKWl8vnOczl8sRhAuRJ/E90/VXA1g+xkhA==
        -----END PUBLIC KEY-----
        """;

    public static bool Verify(string json, string? signature) =>
        EcdsaManifestVerifier.Verify(PublicKey, DomainSeparator, json, signature);
}
