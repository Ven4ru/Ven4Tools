using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Ven4Tools.Models
{
    public class MasterCatalog
    {
        public int Version { get; set; } = 1;

        public string LastUpdated { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        public List<App> Apps { get; set; } = new List<App>();

        public List<CatalogChangelogEntry> Changelog { get; set; } = new List<CatalogChangelogEntry>();

        [JsonIgnore]
        public string Source { get; set; } = "online";
    }

    public class CatalogChangelogEntry
    {
        public int Version { get; set; }

        public string Date { get; set; } = string.Empty;

        public List<string> AddedApps { get; set; } = new List<string>();

        public string Message { get; set; } = string.Empty;
    }
}
