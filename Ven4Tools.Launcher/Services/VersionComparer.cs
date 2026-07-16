namespace Ven4Tools.Launcher.Services
{
    internal static class VersionComparer
    {
        /// <summary>
        /// Returns positive if v1 > v2, negative if v1 < v2, 0 if equal.
        /// Stable version ranks higher than same-number pre-release: "3.1.0" > "3.1.0-pre".
        /// </summary>
        public static int Compare(string? v1, string? v2)
        {
            // Целостность входных данных: версии приходят из внешних источников
            // (теги GitHub-релизов, version.json CDN, метаданные exe). null/пустая
            // строка не должна ронять сравнение через NullReferenceException —
            // трактуем её как «0» (отсутствие версии = самая старая), чтобы
            // некорректная версия-кандидат никогда не считалась «новее» реальной.
            v1 ??= "";
            v2 ??= "";
            var parts1 = v1.Split('.');
            var parts2 = v2.Split('.');
            for (int i = 0; i < System.Math.Max(parts1.Length, parts2.Length); i++)
            {
                string s1 = i < parts1.Length ? parts1[i].Split('-')[0] : "0";
                string s2 = i < parts2.Length ? parts2[i].Split('-')[0] : "0";
                int n1 = int.TryParse(s1, out var x) ? x : 0;
                int n2 = int.TryParse(s2, out var y) ? y : 0;
                if (n1 != n2) return n1.CompareTo(n2);
            }
            bool v1Pre = v1.Contains('-');
            bool v2Pre = v2.Contains('-');
            if (v1Pre != v2Pre) return v1Pre ? -1 : 1;
            return 0;
        }

        public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;
    }
}
