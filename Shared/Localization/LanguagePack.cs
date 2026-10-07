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
        // В кэш идут только короткие однострочные строки. Журнал в окне — один растущий
        // многострочный текст: кэшировать каждое его состояние значило бы копить мегабайты.
        private const int MaxCachedLength = 400;
        // Шаблоны к длинному многострочному тексту не примеряются: регулярное выражение на
        // десятках килобайт журнала — это заметная пауза в интерфейсе. Такой текст
        // переводится построчно.
        private const int MaxPatternLength = 2000;

        private readonly Dictionary<string, string> _literals;
        private readonly Dictionary<string, string> _plainLiterals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Pattern>> _byPrefix = new(StringComparer.Ordinal);
        private readonly List<Pattern> _floating = new();
        private readonly ConcurrentDictionary<string, (string Text, bool Complete)> _cache = new(StringComparer.Ordinal);

        public string Language { get; }
        public int LiteralCount => _literals.Count;
        public int PatternCount { get; }

        /// <summary>Вызывается для каждой русской строки, которой не нашлось перевода.</summary>
        public Action<string>? Missed { get; set; }

        // В языке перевода дробная часть отделяется точкой. Числа приходят на экран уже
        // отформатированными по правилам Windows (в русской — с запятой), и менять правила
        // для всей программы нельзя: от них зависит разбор вывода других программ.
        private readonly bool _decimalPoint;

        private LanguagePack(string language, Dictionary<string, string> literals, IEnumerable<(string Source, string Target)> patterns)
        {
            Language = language;
            _decimalPoint = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
            _literals = literals;

            // Строка в коде часто начинается со значка («✅ Готово»), а на экран попадает
            // без него: значок рисуется отдельным элементом. Для таких случаев те же
            // строки записываются второй раз — без значка в начале исходника и перевода.
            foreach (var (source, target) in literals)
            {
                int mark = MarkLength(source);
                if (mark == 0 || mark >= source.Length) continue;
                string plain = source[mark..];
                if (HasCyrillic(plain) && !literals.ContainsKey(plain))
                    _plainLiterals.TryAdd(plain, target[Math.Min(MarkLength(target), target.Length)..]);
            }

            var original = patterns.ToList();
            var all = new List<(string Source, string Target, bool Derived)>(original.Count * 2);
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (source, target) in original)
            {
                if (known.Add(source)) all.Add((source, target, false));
            }
            foreach (var (source, target) in original)
            {
                int mark = MarkLength(source);
                if (mark == 0 || mark >= source.Length) continue;
                string plain = source[mark..];
                if (HasCyrillic(plain) && known.Add(plain))
                    all.Add((plain, target[Math.Min(MarkLength(target), target.Length)..], true));
            }

            int count = 0;
            // Более определённые шаблоны (больше обычного текста) проверяются первыми:
            // «{0} МБ свободно» должен победить «{0} МБ».
            foreach (var (source, target, derived) in all.OrderByDescending(p => LiteralLength(p.Source)))
            {
                var pattern = Pattern.TryCreate(source, target);
                if (pattern == null) continue;
                if (!derived) count++;
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
            string result = TranslateCore(text, 0, out bool complete);
            if (!complete && Missed != null) ReportMissed(text);
            return result;
        }

        // О многострочном тексте сообщаем построчно: журнал в окне растёт, и целиком он
        // «новая строка без перевода» при каждом добавлении.
        private void ReportMissed(string text)
        {
            if (!text.Contains('\n')) { Missed?.Invoke(text); return; }
            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || !HasCyrillic(trimmed)) continue;
                TranslateCore(trimmed, 0, out bool lineComplete);
                if (!lineComplete) Missed?.Invoke(trimmed);
            }
        }

        private string TranslateCore(string text, int depth, out bool complete)
        {
            complete = true;
            if (!HasCyrillic(text)) return text;
            if (_literals.TryGetValue(text, out string? exact)) return exact;

            bool multiline = text.Contains('\n');
            bool cacheable = depth == 0 && !multiline && text.Length <= MaxCachedLength;
            if (cacheable && _cache.TryGetValue(text, out var hit))
            {
                complete = hit.Complete;
                return hit.Text;
            }

            string result = TranslateUncached(text, depth, multiline, out complete);
            if (cacheable && _cache.Count < CacheLimit) _cache[text] = (result, complete);
            return result;
        }

        private string TranslateUncached(string text, int depth, bool multiline, out bool complete)
        {
            complete = true;

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

            if (depth < MaxDepth && (!multiline || text.Length <= MaxPatternLength)
                && TryPattern(text, depth, out string? byPattern)) return byPattern!;

            if (_plainLiterals.TryGetValue(text, out string? withoutMark)) return withoutMark;

            if (!multiline)
            {
                // Значок перед текстом («• …», «⚠ …») и многоточие или двоеточие после него
                // часто приставляются в коде отдельно от самой строки: ищем перевод без
                // них и возвращаем их на место. Сначала без начала, затем без конца, затем
                // без того и другого.
                int lead = MarkLength(text);
                int tail = lead < text.Length ? TailLength(text, lead) : 0;
                if (lead > 0 && lead < text.Length && TryBody(text[lead..], depth, out string? body))
                    return string.Concat(text.AsSpan(0, lead), body);
                if (tail > 0 && TryBody(text[..^tail], depth, out body))
                    return string.Concat(body, text.AsSpan(text.Length - tail));
                if (lead > 0 && tail > 0 && lead < text.Length - tail && TryBody(text[lead..^tail], depth, out body))
                    return string.Concat(text.AsSpan(0, lead), body, text.AsSpan(text.Length - tail));

                // Строка, собранная из частей через разделитель («Чтение — 512 МБ/с»,
                // «Установлено: Имя»): шаблона для неё в коде нет, но сами части в пакете
                // есть. Переводится то, что нашлось; остальное остаётся как есть.
                if (depth < MaxDepth && TryParts(text, depth, out string? joined, out bool partsComplete))
                {
                    complete = partsComplete;
                    return joined!;
                }
            }

            if (multiline)
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

        // Ровно «цифры, запятая, одна-две цифры»: дробное число, а не перечисление.
        private static bool IsDecimalWithComma(string value)
        {
            int comma = value.IndexOf(',');
            if (comma <= 0 || comma != value.LastIndexOf(',')) return false;
            int fraction = value.Length - comma - 1;
            if (fraction is < 1 or > 2) return false;
            for (int i = 0; i < value.Length; i++)
            {
                if (i != comma && !char.IsAsciiDigit(value[i])) return false;
            }
            return true;
        }

        private static readonly string[] Separators = { " — ", " · ", " | ", ": ", ", " };

        private bool TryBody(string body, int depth, out string? result)
        {
            if (_literals.TryGetValue(body, out result) || _plainLiterals.TryGetValue(body, out result)) return true;
            if (depth < MaxDepth && TryPattern(body, depth, out result)) return true;
            result = null;
            return false;
        }

        private bool TryParts(string text, int depth, out string? result, out bool complete)
        {
            foreach (string separator in Separators)
            {
                int at = text.IndexOf(separator, StringComparison.Ordinal);
                if (at <= 0 || at + separator.Length >= text.Length) continue;

                string[] parts = text.Split(separator);
                bool changed = false, all = true;
                for (int i = 0; i < parts.Length; i++)
                {
                    string part = parts[i];
                    if (!HasCyrillic(part))
                    {
                        if (_decimalPoint && IsDecimalWithComma(part)) { parts[i] = part.Replace(',', '.'); changed = true; }
                        continue;
                    }

                    string translated = TranslateCore(part, depth + 1, out bool partComplete);
                    // «Слово:» в пакете обычно записано вместе с двоеточием.
                    if (!partComplete && separator == ": " && i < parts.Length - 1)
                    {
                        string withColon = TranslateCore(part + ":", depth + 1, out bool colonComplete);
                        if (colonComplete && withColon.EndsWith(':')) { translated = withColon[..^1]; partComplete = true; }
                    }

                    all &= partComplete;
                    if (!ReferenceEquals(translated, part) && translated != part) { parts[i] = translated; changed = true; }
                }
                if (!changed) continue;

                result = string.Join(separator, parts);
                complete = all;
                return true;
            }
            result = null;
            complete = false;
            return false;
        }

        // Сколько знаков в начале строки — значок или маркер, а не сам текст. Скобка или
        // кавычка — часть самой строки («[Сеть] …», «„Имя“ …»), на них счёт останавливается.
        private static int MarkLength(string text)
        {
            int length = 0;
            while (length < text.Length)
            {
                char c = text[length];
                // Отметка времени журнала «[12:34:56] » — тоже не текст: в скобках только цифры и знаки.
                if (c == '[')
                {
                    int close = text.IndexOf(']', length);
                    if (close > length && close - length <= 24 && !ContainsLetter(text, length + 1, close))
                    {
                        length = close + 1;
                        continue;
                    }
                }
                if (IsTextChar(c) || IsOpeningMark(c)) break;
                length++;
            }
            return length;
        }

        // Буква или цифра текста. Значки вроде «ℹ» и «№» по правилам Юникода тоже «буквы»
        // (блок буквоподобных символов), но для нас это оформление, а не текст.
        private static bool IsTextChar(char c) => char.IsLetterOrDigit(c) && c is not (>= '\u2100' and <= '\u214F');

        private static bool ContainsLetter(string text, int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                if (char.IsLetter(text[i])) return true;
            }
            return false;
        }

        // То же с конца строки: многоточие, двоеточие, значок после текста.
        private static int TailLength(string text, int lead)
        {
            int length = 0;
            while (length < text.Length - lead - 1)
            {
                char c = text[text.Length - 1 - length];
                if (IsTextChar(c) || IsClosingMark(c)) break;
                length++;
            }
            return length;
        }

        private static bool IsOpeningMark(char c) => c is '[' or '(' or '{' or '«' or '"' or '\'' or '“' or '„' or '<';

        private static bool IsClosingMark(char c) => c is ']' or ')' or '}' or '»' or '"' or '\'' or '”' or '“' or '>' or '%';

        // Строка многострочного текста — тот же поиск, что и для отдельной строки:
        // целиком, без пробелов по краям, по шаблонам, без значка в начале.
        private string TranslateLine(string line, int depth, out bool complete) =>
            TranslateCore(line, depth, out complete);

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
                        if (HasCyrillic(value)) builder.Append(pack.TranslateCore(value, depth + 1, out _));
                        // Число с десятичной запятой («12,7 МБ»): в английском тексте — точка.
                        else if (pack._decimalPoint && IsDecimalWithComma(value)) builder.Append(value.Replace(',', '.'));
                        else builder.Append(value);
                    }
                }
                result = builder.ToString();
                return true;
            }

        }
    }
}
