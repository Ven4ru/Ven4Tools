using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Ven4Tools.Launcher.Models
{
    /// <summary>
    /// Файловый манифест публикации клиента (client-manifest.json): полный список
    /// файлов сборки с их SHA256 и размером. Отличается по назначению от
    /// <see cref="CdnVersionInfo"/>: тот описывает архив релиза ЦЕЛИКОМ (одна ссылка +
    /// один хеш), а этот — его содержимое ПОФАЙЛОВО, что и позволяет качать при
    /// обновлении только изменившиеся файлы (см. ClientDeltaPlanner).
    ///
    /// Публикуется на CDN рядом с zip-архивом релиза вместе с подписью
    /// client-manifest.json.sig (ECDSA, домен «Ven4Tools.ClientManifest.v1» —
    /// отдельный ключ и отдельный домен от version.json, см. ClientManifestVerifier).
    /// Этой же моделью описывается локальный кэш установленной версии
    /// (InstalledManifestStore), но там он хранится без подписи.
    /// </summary>
    public class ClientFileManifest
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("generatedAt")]
        public string? GeneratedAt { get; set; }

        [JsonPropertyName("files")]
        public List<ClientManifestFileEntry>? Files { get; set; }
    }

    public class ClientManifestFileEntry
    {
        /// <summary>
        /// Путь относительно корня публикации, всегда через '/' и без начального
        /// слэша (например «Resources/Fonts/Inter.ttf»). Формат задаёт генератор
        /// манифеста (Tools/ClientManifestSigner generate); лаунчер дополнительно
        /// проверяет его на безопасность перед записью на диск, потому что путь
        /// из манифеста напрямую превращается в путь файла в папке клиента.
        /// </summary>
        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
