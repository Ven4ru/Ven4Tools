using System.Collections.Generic;

namespace Ven4Tools.Models
{
    public class SourceOrderSettings
    {
        public const string Winget = "winget";
        public const string Choco  = "choco";
        public const string Direct = "direct";

        public static readonly List<string> AllSources =
            new() { Winget, Direct, Choco };

        public static readonly Dictionary<string, string> Labels = new()
        {
            [Winget] = "📦 Winget",
            [Choco]  = "🍫 Chocolatey",
            [Direct] = "🔗 Прямая ссылка"
        };

        // "global" — общий порядок, "per_category" — свой основной источник у категории
        public string Mode { get; set; } = "global";

        // Упорядоченный список идентификаторов источников: ["winget","direct","choco"]
        public List<string> GlobalOrder { get; set; } = new(AllSources);

        // Основной источник для категории: "Браузеры" -> "winget".
        // Пустая строка или отсутствие ключа — используется общий порядок.
        public Dictionary<string, string> CategoryPrimary { get; set; } = new();
    }
}
