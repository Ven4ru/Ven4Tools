using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ven4Tools.Services.Diagnostics
{
    public enum CpuSupport { Supported, NotSupported, Unknown }

    internal sealed class CpuMatchResult
    {
        public required CpuSupport Support { get; init; }

        /// <summary>Имя процессора после очистки — то, что сопоставлялось со списком.</summary>
        public required string NormalizedName { get; init; }

        /// <summary>Пояснение сработавшего правила («Core 8–9-го поколения»); null — ни одно не подошло.</summary>
        public string? RuleNote { get; init; }
    }

    /// <summary>
    /// Есть ли процессор в списках Microsoft для Windows 11. Правила — данными в ресурсе
    /// клиента (Resources/Diagnostics/win11_cpu_rules.json, источники указаны в файле), не в каталоге.
    /// Сначала проверяются правила «в списке», затем «точно не в списке»; не подошло ни одно —
    /// честное «нет данных», а не догадка.
    /// </summary>
    internal sealed class Win11CpuMatcher
    {
        internal const string ResourceName = "Ven4Tools.Resources.Diagnostics.win11_cpu_rules.json";

        private readonly List<(Regex Pattern, string Note)> _supported;
        private readonly List<(Regex Pattern, string Note)> _unsupported;

        private Win11CpuMatcher(List<(Regex, string)> supported, List<(Regex, string)> unsupported)
        {
            _supported = supported;
            _unsupported = unsupported;
        }

        public int SupportedRuleCount => _supported.Count;
        public int UnsupportedRuleCount => _unsupported.Count;

        private static readonly Lazy<Win11CpuMatcher> _embedded = new(LoadEmbedded);
        public static Win11CpuMatcher Embedded => _embedded.Value;

        private static Win11CpuMatcher LoadEmbedded()
        {
            try
            {
                using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    return Parse(reader.ReadToEnd());
                }
                AppLogger.Write($"[Windows 11] Ресурс списка процессоров не найден: {ResourceName}");
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "Win11CpuMatcher.LoadEmbedded");
            }
            return new Win11CpuMatcher(new List<(Regex, string)>(), new List<(Regex, string)>());
        }

        /// <summary>Разбор правил. Неверное регулярное выражение пропускается (и пишется в журнал), остальные работают.</summary>
        internal static Win11CpuMatcher Parse(string json)
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return new Win11CpuMatcher(ReadRules(doc.RootElement, "supported"), ReadRules(doc.RootElement, "unsupported"));
        }

        private static List<(Regex, string)> ReadRules(JsonElement root, string property)
        {
            var list = new List<(Regex, string)>();
            if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string? pattern = item.TryGetProperty("pattern", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                string note = item.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(pattern)) continue;
                try
                {
                    list.Add((new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)), note));
                }
                catch (ArgumentException ex)
                {
                    AppLogger.Write($"[Windows 11] Пропущено неверное правило процессора «{pattern}»: {ex.Message}");
                }
            }
            return list;
        }

        private static readonly Regex TrademarkRegex = new(@"\((?:R|TM|C)\)|®|™", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex FrequencyRegex = new(@"@.*$", RegexOptions.CultureInvariant);
        private static readonly Regex CoreCountRegex = new(@"\b[\w]+-Core Processor\b|\bProcessor\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex GraphicsRegex = new(@"\s+(?:with|w/)\s+Radeon.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex CpuWordRegex = new(@"\bCPU\b", RegexOptions.CultureInvariant);
        private static readonly Regex SpacesRegex = new(@"\s+", RegexOptions.CultureInvariant);

        /// <summary>
        /// «Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz» → «Intel Core i7-8700K»;
        /// «AMD Ryzen 7 4800H with Radeon Graphics» → «AMD Ryzen 7 4800H».
        /// </summary>
        internal static string Normalize(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            // Знак убирается без пробела: «FX(tm)-8350» должно стать «FX-8350», а не «FX -8350».
            string s = TrademarkRegex.Replace(name, "");
            s = FrequencyRegex.Replace(s, " ");
            s = GraphicsRegex.Replace(s, " ");
            s = CoreCountRegex.Replace(s, " ");
            s = CpuWordRegex.Replace(s, " ");
            return SpacesRegex.Replace(s, " ").Trim();
        }

        public CpuMatchResult Match(string? processorName)
        {
            string normalized = Normalize(processorName);
            if (normalized.Length == 0)
                return new CpuMatchResult { Support = CpuSupport.Unknown, NormalizedName = normalized };

            foreach (var (pattern, note) in _supported)
                if (SafeIsMatch(pattern, normalized))
                    return new CpuMatchResult { Support = CpuSupport.Supported, NormalizedName = normalized, RuleNote = note };

            foreach (var (pattern, note) in _unsupported)
                if (SafeIsMatch(pattern, normalized))
                    return new CpuMatchResult { Support = CpuSupport.NotSupported, NormalizedName = normalized, RuleNote = note };

            return new CpuMatchResult { Support = CpuSupport.Unknown, NormalizedName = normalized };
        }

        private static bool SafeIsMatch(Regex regex, string text)
        {
            try { return regex.IsMatch(text); }
            catch (RegexMatchTimeoutException) { return false; }
        }
    }
}
