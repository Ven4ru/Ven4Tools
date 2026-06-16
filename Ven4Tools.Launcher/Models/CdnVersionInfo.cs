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
    }

    public class CdnLauncherInfo
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("exe_url")]
        public string? ExeUrl { get; set; }

        [JsonPropertyName("exe_fallback")]
        public string? ExeFallback { get; set; }

        [JsonPropertyName("setup_url")]
        public string? SetupUrl { get; set; }

        [JsonPropertyName("setup_fallback")]
        public string? SetupFallback { get; set; }
    }
}
