using System;
using Newtonsoft.Json;

namespace Ven4Tools.Models
{
    public class Consent
    {
        [JsonProperty("allowStats")]
        public bool AllowStats { get; set; }
        
        [JsonProperty("askedAt")]
        public DateTime AskedAt { get; set; }
        
        [JsonProperty("version")]
        public string Version { get; set; } = "1.0";
    }
}