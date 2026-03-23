using System.Collections.Generic;
using Newtonsoft.Json;

namespace Ven4Tools.Models
{
    public class Stats
    {
        [JsonProperty("userAdds")]
        public Dictionary<string, AppStats> UserAdds { get; set; } = new Dictionary<string, AppStats>();
        
        [JsonProperty("overrides")]
        public Dictionary<string, int> Overrides { get; set; } = new Dictionary<string, int>();
        
        [JsonProperty("lastUpdate")]
        public string LastUpdate { get; set; } = "";
    }
    
    public class AppStats
    {
        [JsonProperty("count")]
        public int Count { get; set; }
        
        [JsonProperty("wingetIds")]
        public List<string> WingetIds { get; set; } = new List<string>();
        
        [JsonProperty("urls")]
        public List<string> Urls { get; set; } = new List<string>();
    }
}
