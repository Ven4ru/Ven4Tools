using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ven4Tools.Localization
{
    /// <summary>
    /// Языковой пакет: перевод интерфейса с русского на другой язык.
    ///
    /// Русский текст в коде и разметке — исходник и одновременно ключ: программа пишется
    /// по-русски как раньше, а пакет говорит, чем заменить каждую строку при показе.
    /// Поэтому русской версии пакет не нужен вовсе, а перевод поставляется отдельным
    /// файлом — его можно установить сразу или скачать позже.
    ///
    /// В пакете два вида записей:
    /// <list type="bullet">
    /// <item>цельные строки — ищутся по точному совпадению;</item>
    /// <item>шаблоны с подстановками («Установлено: {0}, ошибок: {1}») — на экране в
    /// них уже стоят значения, поэтому шаблон превращается в регулярное выражение, а
    /// найденные значения переносятся в перевод (и сами переводятся, если это текст).</item>
    /// </list>
    /// Многострочный текст, которого нет в пакете целиком, переводится построчно: так
    /// находят перевод сообщения, собранные в коде из нескольких кусков.
    ///
    /// Объект неизменяем после создания и безопасен для вызова из любых потоков.
    /// </summary>
    public sealed class LanguagePack
    {
        private const int MaxDepth = 3;
        private const int CacheLimit = 8000;

        private readonly Dictionary<string, string> _literals;
        private readonly Dictionary<string, List<Pattern>> _byPrefix = new(StringComparer.Ordinal);
        private readonly List<Pattern> _floating = new();
        private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

        public string Language { get; }
        public int LiteralCount => _literals.Count;
        public int PatternCount { get; }

        /// <summary>Вызывается для каждой русской строки, которой не нашлось перевода.</summary>
        public Action<string>? Missed { get; set; }

        private LanguagePack(string language, Dictionary<string, string> literals, IEnumerable<(string Source, string Target)> patterns)
        {
            Language = language;
            _literals = literals;
            int count = 0;
            // Более определённые шаблоны (больше обычного текста) проверяются первыми:
            // «{0} МБ свободно» должен победить «{0} МБ».
            foreach (var (source, target) in patterns.OrderByDescending(p => LiteralLength(p.Source)))
            {
                var pattern = Pattern.TryCreate(source, target);
                if (pattern == null) continue;
                count++;
                if (pattern.Prefix.Length >= PrefixLength)
                {
                    string key = pattern.Prefix[..PrefixLength];
                    if (!_byPrefix.TryGetValue(key, out var list)) _byPrefix[key] = list = new List<Pattern>();
                    list.Add(pattern);
                }
                else _floating.Add(pattern);
            }
            PatternCount = count;
        }

        private const int PrefixLength = 3;

        private static int LiteralLength(string template) => Regex.Replace(template, @"\{\d+\}", "").Length;

        /// <summary>Пустой пакет: ничего не переводит, но сообщает обо всех русских строках (режим сбора).</summary>
        public static LanguagePack Empty(string language) =>
            new(language, new Dictionary<string, string>(StringComparer.Ordinal), Array.Empty<(string, string)>());

        /// <summary>
        /// Разбор файла пакета. Бросает исключение на повреждённом файле — вызывающий
        /// решает, что делать (обычно остаться на русском).
        /// </summary>
        public static LanguagePack Parse(byte[] utf8Json)
        {
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            string language = root.TryGetProperty("lang", out var lang) ? lang.GetString() ?? "en" : "en";

            var literals = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("literals", out var literalsElement))
            {
                foreach (var pair in literalsElement.EnumerateObject())
                {
                    string? value = pair.Value.GetString();
                    if (value != null) literals[pair.Name] = value;
                }
            }

            var patterns = new List<(string, string)>();
            if (root.TryGetProperty("patterns", out var patternsElement))
            {
                foreach (var item in patternsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 2) continue;
                    string? source = item[0].GetString();
                    string? target = item[1].GetString();
                    if (source != null && target != null) patterns.Add((source, target));
                }
            }
            return new LanguagePack(language, literals, patterns);
        }

        public static bool HasCyrillic(string text)
        {
            foreach (char c in text)
            {
                if (c is >= 'А' and <= 'я' or 'ё' or 'Ё') return true;
            }
            return false;
        }

        /// <summary>Перевод строки; строка без русского текста возвращается как есть.</summary>
        public string Translate(string? text)
        {
            if (string.IsNullOrEmpty(text) || !HasCyrillic(text)) return text ?? "";
            if (_cache.TryGetValue(text, out string? cached)) return cached;

            string result = TranslateCore(text, 0, out bool complete);
            if (!complete) Missed?.Invoke(text);
            if (_cache.Count < CacheLimit) _cache[text] = result;
            return result;
        }

        private string TranslateCore(string text, int depth, out bool complete)
        {
            complete = true;
            if (!HasCyrillic(text)) return text;
            if (_literals.TryGetValue(text, out string? exact)) return exact;

            // Пробелы и переводы строк по краям не входят в ключ: на экран строка
            // часто попадает с отступом или хвостовым переводом строки.
            int start = 0, end = text.Length;
            while (start < end && char.IsWhiteSpace(text[start])) start++;
            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
            if (start > 0 || end < text.Length)
            {
                string core = text[start..end];
                string inner = TranslateCore(core, depth, out complete);
                return ReferenceEquals(inner, core) ? text : string.Concat(text.AsSpan(0, start), inner, text.AsSpan(end));
            }

            if (depth < MaxDepth && TryPattern(text, depth, out string? byPattern)) return byPattern!;

            if (text.Contains('\n'))
            {
                string[] lines = text.Split('\n');
                bool changed = false, all = true;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    string translated = TranslateLine(line, depth, out bool lineComplete);
                    all &= lineComplete;
                    if (!ReferenceEquals(translated, line)) { lines[i] = translated; changed = true; }
                }
                complete = all;
                return changed ? string.Join('\n', lines) : text;
            }

            complete = false;
            return text;
        }

        private string TranslateLine(string line, int depth, out bool complete)
        {
            complete = true;
            if (!HasCyrillic(line)) return line;
            if (_literals.TryGetValue(line, out string? exact)) return exact;

            int start = 0, end = line.Length;
            while (start < end && char.IsWhiteSpace(line[start])) start++;
            while (end > start && char.IsWhiteSpace(line[end - 1])) end--;
            string core = start > 0 || end < line.Length ? line[start..end] : line;

            string? translated = null;
            if (!ReferenceEquals(core, line) && _literals.TryGetValue(core, out string? trimmed)) translated = trimmed;
            else if (depth < MaxDepth && TryPattern(core, depth, out string? byPattern)) translated = byPattern;

            if (translated == null) { complete = false; return line; }
            return ReferenceEquals(core, line) ? translated : string.Concat(line.AsSpan(0, start), translated, line.AsSpan(end));
        }

        private bool TryPattern(string text, int depth, out string? result)
        {
            if (text.Length >= PrefixLength && _byPrefix.TryGetValue(text[..PrefixLength], out var candidates))
            {
                foreach (var pattern in candidates)
                {
                    if (pattern.TryApply(text, this, depth, out result)) return true;
                }
            }
            foreach (var pattern in _floating)
            {
                if (pattern.TryApply(text, this, depth, out result)) return true;
            }
            result = null;
            return false;
        }

        private sealed class Pattern
        {
            private readonly Regex _regex;
            private readonly string[] _targetParts;   // куски перевода между подстановками
            private readonly int[] _targetHoles;      // номер подстановки после каждого куска
            private readonly string _anchor;          // самый длинный кусок исходника: быстрая отсечка

            public string Prefix { get; }

            private Pattern(Regex regex, string prefix, string anchor, string[] targetParts, int[] targetHoles)
            {
                _regex = regex;
                Prefix = prefix;
                _anchor = anchor;
                _targetParts = targetParts;
                _targetHoles = targetHoles;
            }

            public static Pattern? TryCreate(string source, string target)
            {
                var sourceParts = Split(source, out var sourceHoles);
                var targetParts = Split(target, out var targetHoles);
                if (sourceHoles.Count == 0) return null;
                // В переводе не может быть подстановки, которой нет в исходнике.
                if (targetHoles.Any(h => !sourceHoles.Contains(h))) return null;

                var regex = new StringBuilder("^");
                for (int i = 0; i < sourceParts.Count; i++)
                {
                    regex.Append(Regex.Escape(sourceParts[i]));
                    if (i < sourceHoles.Count) regex.Append("(?<h").Append(sourceHoles[i]).Append(">.*?)");
                }
                regex.Append('$');

                string anchor = sourceParts.OrderByDescending(p => p.Length).First();
                if (anchor.Length == 0) return null;
                try
                {
                    return new Pattern(
                        new Regex(regex.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),
                        sourceParts[0], anchor, targetParts.ToArray(), targetHoles.ToArray());
                }
                catch (ArgumentException) { return null; }
            }

            // "Текст {0} и {1}" → куски ["Текст ", " и ", ""], подстановки [0, 1]. {{ и }} — буквальные скобки.
            private static List<string> Split(string template, out List<int> holes)
            {
                var parts = new List<string>();
                holes = new List<int>();
                var current = new StringBuilder();
                for (int i = 0; i < template.Length; i++)
                {
                    char c = template[i];
                    if (c == '{' && i + 1 < template.Length && template[i + 1] == '{') { current.Append('{'); i++; continue; }
                    if (c == '}' && i + 1 < template.Length && template[i + 1] == '}') { current.Append('}'); i++; continue; }
                    if (c == '{')
                    {
                        int close = template.IndexOf('}', i);
                        if (close > i && int.TryParse(template.AsSpan(i + 1, close - i - 1), out int hole))
                        {
                            parts.Add(current.ToString());
                            current.Clear();
                            holes.Add(hole);
                            i = close;
                            continue;
                        }
                    }
                    current.Append(c);
                }
                parts.Add(current.ToString());
                return parts;
            }

            public bool TryApply(string text, LanguagePack pack, int depth, out string? result)
            {
                result = null;
                if (!text.Contains(_anchor, StringComparison.Ordinal)) return false;
                Match match;
                try { match = _regex.Match(text); }
                catch (RegexMatchTimeoutException) { return false; }
                if (!match.Success) return false;

                var builder = new StringBuilder(text.Length + 16);
                for (int i = 0; i < _targetParts.Length; i++)
                {
                    builder.Append(_targetParts[i]);
                    if (i < _targetHoles.Length)
                    {
                        string value = match.Groups["h" + _targetHoles[i]].Value;
                        // Значение само может быть текстом интерфейса («Статус: Установлено»).
                        builder.Append(HasCyrillic(value) ? pack.TranslateCore(value, depth + 1, out _) : value);
                    }
                }
                result = builder.ToString();
                return true;
            }
        }
    }
}
