using System.Collections.Generic;

namespace Ven4Tools.Models
{
    public class SourceOrderSettings
    {
        public const string Winget = "winget";
        public const string Choco  = "choco";
        public const string Scoop  = "scoop";
        public const string Direct = "direct";

        public static readonly List<string> AllSources =
            new() { Winget, Choco, Scoop, Direct };

        public static readonly Dictionary<string, string> Labels = new()
        {
            [Winget] = "📦 Winget",
            [Choco]  = "🍫 Chocolatey",
            [Scoop]  = "🪣 Scoop",
            [Direct] = "🔗 Прямая ссылка"
        };

        // "global" or "per_category"
        public string Mode { get; set; } = "global";

        // Ordered list of source IDs: ["winget","choco","scoop","direct"]
        public List<string> GlobalOrder { get; set; } = new(AllSources);

        // Per-category primary source: "Браузеры" -> "winget"
        // Empty string or missing = use global order
        public Dictionary<string, string> CategoryPrimary { get; set; } = new();
    }
}
