using System;
using Newtonsoft.Json;

namespace Ven4Tools.Models
{
    public class HistoryEntry
    {
        [JsonProperty("appId")]
        public string AppId { get; set; } = "";

        [JsonProperty("appName")]
        public string AppName { get; set; } = "";

        [JsonProperty("source")]
        public string Source { get; set; } = "winget";

        [JsonProperty("category")]
        public string Category { get; set; } = "";

        [JsonProperty("machineName")]
        public string MachineName { get; set; } = "";

        [JsonProperty("installedAt")]
        public DateTime InstalledAt { get; set; } = DateTime.Now;

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonIgnore]
        public string SourceLabel => Source switch
        {
            "winget" => "📦 Winget",
            "choco"  => "🍫 Chocolatey",
            "scoop"  => "🪣 Scoop",
            "direct" => "🔗 Direct",
            "cache"  => "🔌 Кэш",
            _        => Source
        };

        [JsonIgnore]
        public string DateLabel => InstalledAt.ToString("dd.MM.yyyy HH:mm");

        [JsonIgnore]
        public string StatusIcon => Success ? "✅" : "❌";

        [JsonIgnore]
        public string ActionVerb => (Success ? "install " : "failed  ");

        // "PC-Name : install AppName [dd.MM.yyyy HH:mm]"
        [JsonIgnore]
        public string ActionLine =>
            $"{MachineName} : {(Success ? "install" : "failed")} {AppName}  [{DateLabel}]";
    }
}
