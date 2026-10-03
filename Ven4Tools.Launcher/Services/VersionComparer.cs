namespace Ven4Tools.Launcher.Services
{
    internal static class VersionComparer
    {
        /// <summary>
        /// Возвращает положительное число, если v1 > v2, отрицательное, если v1 &lt; v2,
        /// и 0 при равенстве. Стабильная версия старше предрелизной с тем же номером:
        /// "3.1.0" > "3.1.0-pre".
        /// </summary>
        public static int Compare(string? v1, string? v2)
        {
            // Целостность входных данных: версии приходят из внешних источников
            // (теги GitHub-релизов, version.json CDN, метаданные exe). null/пустая
            // строка не должна ронять сравнение через NullReferenceException —
            // трактуем её как «0» (отсутствие версии = самая старая), чтобы
            // некорректная версия-кандидат никогда не считалась «новее» реальной.
            var (core1, pre1) = Split(v1);
            var (core2, pre2) = Split(v2);
            var parts1 = core1.Split('.');
            var parts2 = core2.Split('.');
            for (int i = 0; i < System.Math.Max(parts1.Length, parts2.Length); i++)
            {
                string s1 = i < parts1.Length ? parts1[i] : "0";
                string s2 = i < parts2.Length ? parts2[i] : "0";
                int n1 = int.TryParse(s1, out var x) ? x : 0;
                int n2 = int.TryParse(s2, out var y) ? y : 0;
                if (n1 != n2) return n1.CompareTo(n2);
            }
            if (pre1.Length == 0 || pre2.Length == 0)
            {
                if (pre1.Length == 0 && pre2.Length == 0) return 0;
                return pre1.Length == 0 ? 1 : -1;
            }
            return ComparePrerelease(pre1, pre2);
        }

        // Номер и предрелизная метка разбираются отдельно. Раньше строка целиком
        // делилась по точкам: «5.3.0-beta.2» давала четвёртый компонент «2», и
        // предрелиз оказывался НОВЕЕ стабильной «5.3.0», а «5.3.0-beta» и «5.3.0-rc»
        // считались равными. Сборочные метаданные («+хеш коммита» в версии exe) в
        // сравнении не участвуют: «5.3.1+abc» раньше читалось как патч 0.
        private static (string Core, string Prerelease) Split(string? version)
        {
            string v = (version ?? "").Trim();
            int plus = v.IndexOf('+');
            if (plus >= 0) v = v[..plus];
            int dash = v.IndexOf('-');
            return dash < 0 ? (v, "") : (v[..dash], v[(dash + 1)..]);
        }

        // Порядок предрелизных меток — как в semver: по идентификаторам через точку,
        // числовые сравниваются как числа и младше буквенных, при равном начале
        // короче — младше («beta» < «beta.2» < «rc»).
        private static int ComparePrerelease(string a, string b)
        {
            var ia = a.Split('.');
            var ib = b.Split('.');
            for (int i = 0; i < System.Math.Min(ia.Length, ib.Length); i++)
            {
                bool na = int.TryParse(ia[i], out int xa);
                bool nb = int.TryParse(ib[i], out int xb);
                int c = na && nb ? xa.CompareTo(xb)
                    : na ? -1
                    : nb ? 1
                    : string.Compare(ia[i], ib[i], System.StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c < 0 ? -1 : 1;
            }
            return ia.Length.CompareTo(ib.Length);
        }

        public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;
    }
}
