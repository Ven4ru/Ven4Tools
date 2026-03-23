using System;
using Newtonsoft.Json;

namespace Ven4Tools.Models
{
    public class App
    {
        [JsonProperty("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("category")]
        public string Category { get; set; } = "Другое";

        [JsonProperty("wingetId")]
        public string WingetId { get; set; } = string.Empty;

        [JsonProperty("downloadUrl")]
        public string DownloadUrl { get; set; } = string.Empty;

        [JsonProperty("version")]
        public string Version { get; set; } = string.Empty;

        [JsonProperty("size")]
        public string Size { get; set; } = string.Empty;

        [JsonProperty("official")]
        public bool Official { get; set; } = true;

        [JsonProperty("offlineCapable")]
        public bool OfflineCapable { get; set; } = false;

        [JsonProperty("iconUrl")]
        public string IconUrl { get; set; } = string.Empty;

        [JsonProperty("description")]
        public string Description { get; set; } = string.Empty;

        [JsonIgnore]
        public bool IsSelected { get; set; }

        [JsonIgnore]
        public bool IsUnavailable { get; set; }

        [JsonIgnore]
        public string Status => IsUnavailable ? "❌ Недоступно" : "✅ Доступно";
        
        [JsonIgnore]
        public string Source { get; set; } = "online";
    }
}
