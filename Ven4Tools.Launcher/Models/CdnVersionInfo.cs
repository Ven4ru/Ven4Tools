using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Ven4Tools.Launcher.Models
{
    /// <summary>
    /// Модель version.json с CDN: информация о версиях и ссылки на загрузку
    /// клиента и лаунчера. У ссылки установщика лаунчера есть GitHub-резерв
    /// (<see cref="CdnLauncherInfo.SetupFallback"/>); GitHub-ссылка клиента
    /// строится независимо (см. MainWindow.Versions.cs/UpdateBackgroundService),
    /// поэтому у клиента поля zip_fallback в модели нет.
    /// </summary>
    public class CdnVersionInfo
    {
        [JsonPropertyName("client")]
        public CdnClientInfo? Client { get; set; }

        [JsonPropertyName("launcher")]
        public CdnLauncherInfo? Launcher { get; set; }

        // Текущий IP-адрес cdn.ven4tools.ru — подписан вместе со всем манифестом
        // (доверенное поле). Нужен для варианта «прямой IP в обход DNS»: сам
        // version.json лежит на этом же домене, поэтому если домен заблокируют по
        // DNS отдельно от IP — повторную попытку делаем по этому адресу
        // (см. CdnService/IpPinnedHttpClientFactory). Значение НЕ участвует в
        // allowlist-проверке URL: ссылка загрузки всё равно https://cdn.ven4tools.ru/...
        // и проходит штатную SNI/сертификат-валидацию.
        [JsonPropertyName("cdn_ip")]
        public string? CdnIp { get; set; }

        [JsonPropertyName("revokedClientHashes")]
        public List<string>? RevokedClientHashes { get; set; }

        [JsonPropertyName("historicalClientArchives")]
        public List<HistoricalClientArchive>? HistoricalClientArchives { get; set; }
    }

    public class CdnClientInfo
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("zip_url")]
        public string? ZipUrl { get; set; }

        // Зеркало клиента на хостинге (независимый провайдер, только путь /releases/).
        [JsonPropertyName("zip_mirror_hosting")]
        public string? ZipMirrorHosting { get; set; }

        // SHA256 zip-архива клиента для проверки целостности после скачивания.
        [JsonPropertyName("zip_sha256")]
        public string? ZipSha256 { get; set; }

        // --- Блочное (дельта-) обновление клиента ---
        // Все четыре поля опциональны и заполняются только начиная с релизов, для
        // которых на CDN выложен файловый манифест публикации. Старый version.json
        // без них полностью работоспособен: дельта в этом случае просто недоступна и
        // обновление идёт обычным полным путём (архив целиком). Обратная совместимость
        // здесь обязательна — version.json на CDN обновляется независимо от лаунчера,
        // и лаунчер новой версии обязан работать со старым манифестом, и наоборот.

        // Подписанный файловый манифест публикации (client-manifest.json): список
        // «относительный путь → SHA256 → размер» для каждого файла версии.
        [JsonPropertyName("manifest_url")]
        public string? ManifestUrl { get; set; }

        // ECDSA-подпись манифеста (client-manifest.json.sig), домен
        // «Ven4Tools.ClientManifest.v1» — см. ClientManifestVerifier.
        [JsonPropertyName("manifest_signature_url")]
        public string? ManifestSignatureUrl { get; set; }

        // Базовый URL, под которым лежат ОТДЕЛЬНЫЕ файлы публикации этой версии:
        // https://cdn.ven4tools.ru/client-files/<версия>/. К нему дописывается
        // относительный путь файла из манифеста.
        [JsonPropertyName("files_base_url")]
        public string? FilesBaseUrl { get; set; }

        // Зеркало тех же отдельных файлов на хостинге (независимый провайдер,
        // только путь /releases/): https://ven4tools.ru/releases/client-files/<версия>/.
        // Если поле не задано, цепочка источников дельты вырождается в
        // «CDN-домен → CDN прямой IP» — это рабочий, просто менее живучий вариант.
        [JsonPropertyName("files_base_mirror_hosting")]
        public string? FilesBaseMirrorHosting { get; set; }
    }

    // Самообновление лаунчера идёт только через установщик Ven4Tools.Setup-X.Y.Z.exe,
    // поэтому поля голого exe (exe_url/exe_fallback/exe_sha256) удалены из модели.
    // Если version.json на CDN всё ещё содержит их — они игнорируются при разборе.
    public class CdnLauncherInfo
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("setup_url")]
        public string? SetupUrl { get; set; }

        // GitHub-резерв установщика. Присутствует в version.json всегда — раньше
        // отсутствовал в модели (баг упущения), из-за чего GitHub-ссылка установщика
        // не участвовала в цепочке источников при обнаружении обновления через CDN.
        [JsonPropertyName("setup_fallback")]
        public string? SetupFallback { get; set; }

        // Зеркало установщика на хостинге (независимый провайдер, только путь /releases/).
        [JsonPropertyName("setup_mirror_hosting")]
        public string? SetupMirrorHosting { get; set; }

        // SHA256 установщика лаунчера для проверки целостности после скачивания.
        [JsonPropertyName("setup_sha256")]
        public string? SetupSha256 { get; set; }
    }

    public class HistoricalClientArchive
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }
    }
}
