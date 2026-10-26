using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Ven4Tools.Localization;

namespace Ven4Tools.Services.Diagnostics
{
    /// <summary>Расшифровка одного кода синего экрана: имя Microsoft и простое объяснение.</summary>
    internal sealed class BugCheckDescription
    {
        public required uint Code { get; init; }
        public required string Name { get; init; }
        public required string CauseRu { get; init; }
        public required string CheckRu { get; init; }
        public required string CauseEn { get; init; }
        public required string CheckEn { get; init; }

        /// <summary>Объяснение на языке интерфейса. Текст словаря в языковой пакет не попадает
        /// (сборщик пакета читает только C# и XAML), поэтому английский лежит в самом словаре.</summary>
        public string Cause => UiTranslator.Language == "en" ? CauseEn : CauseRu;
        public string Check => UiTranslator.Language == "en" ? CheckEn : CheckRu;
    }

    /// <summary>
    /// Словарь самых частых кодов синего экрана (Resources/Diagnostics/bugcheck_codes.json,
    /// источник — Bug Check Code Reference от Microsoft, указан в самом файле).
    /// Только справка: дамп не разбирается, диагноз не ставится.
    /// </summary>
    internal sealed class BugCheckDictionary
    {
        internal const string ResourceName = "Ven4Tools.Resources.Diagnostics.bugcheck_codes.json";

        /// <summary>Общая страница Microsoft со всеми кодами — для кода не из словаря.</summary>
        internal const string ReferenceUrl =
            "https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-code-reference2";

        private readonly Dictionary<uint, BugCheckDescription> _byCode;

        private BugCheckDictionary(Dictionary<uint, BugCheckDescription> byCode) => _byCode = byCode;

        public int Count => _byCode.Count;

        private static readonly Lazy<BugCheckDictionary> _embedded = new(LoadEmbedded);

        /// <summary>Словарь из ресурса клиента. Сбой чтения — пустой словарь (все коды «не из словаря»).</summary>
        public static BugCheckDictionary Embedded => _embedded.Value;

        private static BugCheckDictionary LoadEmbedded()
        {
            try
            {
                using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
                if (stream == null)
                {
                    AppLogger.Write($"[BugCheck] Ресурс словаря не найден: {ResourceName}");
                    return new BugCheckDictionary(new Dictionary<uint, BugCheckDescription>());
                }
                using var reader = new StreamReader(stream);
                return Parse(reader.ReadToEnd());
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "BugCheckDictionary.LoadEmbedded");
                return new BugCheckDictionary(new Dictionary<uint, BugCheckDescription>());
            }
        }

        /// <summary>Разбор JSON словаря. Записи без кода, имени или текста пропускаются, а не роняют весь словарь.</summary>
        internal static BugCheckDictionary Parse(string json)
        {
            var map = new Dictionary<uint, BugCheckDescription>();
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!doc.RootElement.TryGetProperty("codes", out var codes) || codes.ValueKind != JsonValueKind.Array)
                return new BugCheckDictionary(map);

            foreach (var item in codes.EnumerateArray())
            {
                string? codeText = GetString(item, "code");
                string? name = GetString(item, "name");
                if (codeText == null || name == null || !TryParseHex(codeText, out uint code)) continue;
                if (!item.TryGetProperty("ru", out var ru) || !item.TryGetProperty("en", out var en)) continue;

                string? causeRu = GetString(ru, "cause"), checkRu = GetString(ru, "check");
                string? causeEn = GetString(en, "cause"), checkEn = GetString(en, "check");
                if (causeRu == null || checkRu == null || causeEn == null || checkEn == null) continue;

                map[code] = new BugCheckDescription
                {
                    Code = code, Name = name,
                    CauseRu = causeRu, CheckRu = checkRu, CauseEn = causeEn, CheckEn = checkEn
                };
            }
            return new BugCheckDictionary(map);
        }

        private static string? GetString(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;

        /// <summary>«0x0000000A», «0xa», «0A» → 10.</summary>
        internal static bool TryParseHex(string text, out uint value)
        {
            string s = text.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Найти описание кода. Коды вида 0x1000007E — тот же сбой, что 0x7E (Microsoft
        /// описывает их одной страницей: старший бит 0x10000000 лишь помечает, что
        /// параметры записаны иначе), поэтому при промахе пробуем код без этого бита.
        /// </summary>
        public BugCheckDescription? Lookup(uint code)
        {
            if (_byCode.TryGetValue(code, out var found)) return found;
            if ((code & 0xF0000000u) == 0x10000000u && _byCode.TryGetValue(code & 0x0FFFFFFFu, out found)) return found;
            return null;
        }

        /// <summary>Код в принятом в журналах виде: 0x0000000A.</summary>
        public static string FormatCode(uint code) => "0x" + code.ToString("X8", CultureInfo.InvariantCulture);

        /// <summary>
        /// Ссылка на справку Microsoft по коду. Адреса отдельных страниц кодов у Microsoft
        /// записаны неединообразно (где-то одно тире, где-то два-три), и угаданный адрес
        /// мог бы вести в никуда, — поэтому ведём в поиск по документации с кодом и именем.
        /// </summary>
        public static string MicrosoftInfoUrl(uint code, string? name)
        {
            string terms = $"bug check 0x{code.ToString("X", CultureInfo.InvariantCulture)}" + (name != null ? " " + name : "");
            return "https://learn.microsoft.com/search/?terms=" + Uri.EscapeDataString(terms);
        }
    }
}
