using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Ven4Tools.Helpers
{
    /// <summary>
    /// Размер установщика из каталога. В master.json это строка — «89.6 MB», «~70 MB»,
    /// «1.2 GB» или пусто, — а для итога по набору нужна сумма, поэтому строка
    /// разбирается в мегабайты. Неразборчивое значение не считается нулём: такие
    /// программы учитываются отдельно как «без размера».
    /// </summary>
    public static class CatalogSize
    {
        private static readonly Regex Pattern = new(
            @"^(?<approx>[~≈])?\s*(?<number>\d+(?:[.,]\d+)?)\s*(?<unit>KB|MB|GB|КБ|МБ|ГБ)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

        public static bool TryParseMegabytes(string? text, out double megabytes) =>
            TryParse(text, out megabytes, out _);

        /// <summary>Размер для показа: «89,6 МБ», «≈ 70 МБ», «1,2 ГБ». Пусто, если размер неизвестен.</summary>
        public static string ToDisplay(string? text) =>
            TryParse(text, out double megabytes, out bool approximate)
                ? (approximate ? "≈ " : "") + Format(megabytes)
                : "";

        public static string Format(double megabytes)
        {
            if (megabytes >= 1024) return (megabytes / 1024).ToString("0.#", Russian) + " ГБ";
            if (megabytes >= 100) return megabytes.ToString("0", Russian) + " МБ";
            if (megabytes >= 1) return megabytes.ToString("0.#", Russian) + " МБ";
            return Math.Max(1, Math.Round(megabytes * 1024)).ToString("0", Russian) + " КБ";
        }

        private static bool TryParse(string? text, out double megabytes, out bool approximate)
        {
            megabytes = 0;
            approximate = false;
            if (string.IsNullOrWhiteSpace(text)) return false;

            Match match = Pattern.Match(text.Trim());
            if (!match.Success) return false;
            if (!double.TryParse(match.Groups["number"].Value.Replace(',', '.'),
                    NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double number))
                return false;

            approximate = match.Groups["approx"].Success;
            megabytes = match.Groups["unit"].Value.ToUpperInvariant() switch
            {
                "KB" or "КБ" => number / 1024,
                "GB" or "ГБ" => number * 1024,
                _ => number
            };
            return true;
        }
    }
}
