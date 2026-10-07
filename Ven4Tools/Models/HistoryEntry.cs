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

        [JsonProperty("installedAt")]
        public DateTime InstalledAt { get; set; } = DateTime.Now;

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonIgnore]
        public string SourceLabel => Source switch
        {
            "winget" => "📦 Winget",
            "choco"  => "🍫 Chocolatey",
            "direct" => "🔗 Прямая ссылка",
            "cache"  => "🔌 Кэш",
            _        => Source
        };

        [JsonIgnore]
        public string DateLabel => InstalledAt.ToString("dd.MM.yyyy HH:mm");

        [JsonIgnore]
        public string StatusIcon => Success ? "✅" : "❌";

        [JsonIgnore]
        // Слово занимает в строке списка место одной ширины (шрифт моноширинный), чтобы
        // названия программ шли ровным столбцом на обоих языках.
        public string ActionVerb => Tr(Success ? "установлено" : "не удалось").PadRight(12);
    }
}
