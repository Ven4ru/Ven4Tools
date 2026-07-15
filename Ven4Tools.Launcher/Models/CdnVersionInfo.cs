using System.Text.Json.Serialization;

namespace Ven4Tools.Launcher.Models
{
    /// <summary>
    /// Модель version.json с CDN: информация о версиях и ссылки на загрузку
    /// клиента и лаунчера. У каждой ссылки есть CDN-вариант и GitHub-резерв.
    /// </summary>
    public class CdnVersionInfo
    {
        [JsonPropertyName("client")]
        public CdnClientInfo? Client { get; set; }

        [JsonPropertyName("launcher")]
        public CdnLauncherInfo? Launcher { get; set; }
    }

    public class CdnClientInfo
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("zip_url")]
        public string? ZipUrl { get; set; }

        [JsonPropertyName("zip_fallback")]
        public string? ZipFallback { get; set; }

        // SHA256 zip-архива клиента для проверки целостности после скачивания.
        [JsonPropertyName("zip_sha256")]
        public string? ZipSha256 { get; set; }
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

        // SHA256 установщика лаунчера для проверки целостности после скачивания.
        [JsonPropertyName("setup_sha256")]
        public string? SetupSha256 { get; set; }
    }
}
