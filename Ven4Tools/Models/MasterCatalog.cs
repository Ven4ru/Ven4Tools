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
}
