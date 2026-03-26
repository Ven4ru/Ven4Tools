using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Ven4Tools.Models
{
    public class MasterCatalog
    {
        [JsonProperty("version")]
        public int Version { get; set; } = 1;

        [JsonProperty("lastUpdated")]
        public string LastUpdated { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        [JsonProperty("apps")]
        public List<App> Apps { get; set; } = new List<App>();
        
        [JsonIgnore]
        public string Source { get; set; } = "online";
    }

    public class CatalogChangelogEntry
    {
        [JsonProperty("version")]
        public int Version { get; set; }

        [JsonProperty("date")]
        public string Date { get; set; } = string.Empty;

        [JsonProperty("addedApps")]
        public List<string> AddedApps { get; set; } = new List<string>();

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;
    }
}