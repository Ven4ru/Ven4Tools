using System.Collections.Generic;
using Newtonsoft.Json;

namespace Ven4Tools.Models
{
    public class Stats
    {
        [JsonProperty("userAdds")]
        public Dictionary<string, AppStats> UserAdds { get; set; } = new();

        [JsonProperty("overrides")]
        public Dictionary<string, int> Overrides { get; set; } = new();

        [JsonProperty("overrideDetails")]
        public Dictionary<string, OverrideDetails> OverrideDetails { get; set; } = new();

        [JsonProperty("lastUpdate")]
        public string LastUpdate { get; set; } = "";
    }

    public class AppStats
    {
        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("wingetIds")]
        public List<string> WingetIds { get; set; } = new();

        [JsonProperty("urls")]
        public List<string> Urls { get; set; } = new();
    }

    public class OverrideDetails
    {
        [JsonProperty("totalOverrides")]
        public int TotalOverrides { get; set; }

        [JsonProperty("successfulInstalls")]
        public int SuccessfulInstalls { get; set; }

        [JsonProperty("wingetSelections")]
        public List<WingetSelection> WingetSelections { get; set; } = new();

        [JsonProperty("urlSelections")]
        public List<UrlSelection> UrlSelections { get; set; } = new();
    }

    public class WingetSelection
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("successCount")]
        public int SuccessCount { get; set; }
    }

    public class UrlSelection
    {
        [JsonProperty("url")]
        public string Url { get; set; } = string.Empty;

        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("successCount")]
        public int SuccessCount { get; set; }
    }
}