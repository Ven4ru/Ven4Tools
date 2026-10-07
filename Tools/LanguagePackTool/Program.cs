using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Собирает все строки интерфейса на русском из исходников клиента и лаунчера — то, что
// нужно перевести, чтобы программа заговорила на другом языке.
//
// Русский текст остаётся в коде и разметке как есть: он и есть «ключ». Перевод хранится
// отдельным файлом (языковой пакет) и подставляется при показе (Shared/Localization).
// Этот инструмент отвечает на вопрос «что именно должно быть в пакете»:
//   - цельные строки — как написаны;
//   - строки с подстановками ($"Установлено: {count}", "..." + x + "...", StringFormat в
//     разметке) — шаблоном с местами {0}, {1}: на экране значения разные, шаблон один.
//
//   dotnet run --project Tools/LanguagePackTool -- extract <корень репозитория> <папка для результата>
//       список строк для перевода (по файлу на клиент и лаунчер);
//   dotnet run --project Tools/LanguagePackTool -- pack <корень репозитория> [--strict]
//       сборка языковых пакетов Localization/packs/*-en.json из таблицы переводов
//       Localization/en.json; --strict — ошибка, если хоть одна строка без перевода.
Console.OutputEncoding = Encoding.UTF8;
string command = args.Length > 0 ? args[0] : "extract";
string root = Path.GetFullPath(args.Length > 1 ? args[1] : ".");
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

if (command == "extract")
{
    string outDir = Path.GetFullPath(args.Length > 2 ? args[2] : Path.Combine(root, "Localization", "sources"));
    Directory.CreateDirectory(outDir);
    foreach (string scope in Extractor.Scopes)
    {
        var found = Extractor.Collect(root, scope);
        var result = new
        {
            literals = found.Literals.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { s = p.Key, where = p.Value.Take(3) }),
            formats = found.Formats.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { s = p.Key, where = p.Value.Take(3) }),
        };
        File.WriteAllText(Path.Combine(outDir, scope + ".json"), JsonSerializer.Serialize(result, jsonOptions), new UTF8Encoding(false));
        Console.WriteLine($"{scope}: строк {found.Literals.Count}, шаблонов {found.Formats.Count}, " +
                          $"знаков {found.Literals.Keys.Sum(k => k.Length) + found.Formats.Keys.Sum(k => k.Length)}");
    }
    return 0;
}

if (command == "pack")
{
    bool strict = args.Contains("--strict");
    int missingTotal = 0;
    foreach (string scope in Extractor.Scopes)
    {
        var (json, missing) = PackBuilder.Build(root, scope, "en");
        string path = Path.Combine(root, "Localization", "packs", $"{scope}-en.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Байты файла — то, с чем сверяется вшитая в программу контрольная сумма:
        // без BOM, переводы строк только \n, независимо от машины сборки.
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(json));
        Console.WriteLine($"{scope}: пакет {new FileInfo(path).Length / 1024} КБ, без перевода {missing.Count}");
        foreach (string source in missing.Take(strict ? 40 : 5)) Console.WriteLine("   нет перевода: " + source.Replace("\n", "\\n"));
        missingTotal += missing.Count;
    }
    return strict && missingTotal > 0 ? 1 : 0;
}

if (command == "missing")
{
    // Заготовка для переводчика: строки без перевода с пустым полем "t".
    string outFile = Path.GetFullPath(args.Length > 2 ? args[2] : "missing.json");
    var missing = new SortedSet<string>(StringComparer.Ordinal);
    foreach (string scope in Extractor.Scopes) missing.UnionWith(PackBuilder.Build(root, scope, "en").Missing);
    File.WriteAllText(outFile, JsonSerializer.Serialize(missing.Select(s => new PackBuilder.Entry(s, "")), jsonOptions), new UTF8Encoding(false));
    Console.WriteLine($"без перевода {missing.Count}: {outFile}");
    return 0;
}

if (command == "merge")
{
    // Заполненная заготовка — в общую таблицу переводов. Пустые переводы пропускаются.
    if (args.Length < 3) { Console.Error.WriteLine("Нужен файл с переводами."); return 2; }
    var table = PackBuilder.LoadTranslations(root, "en");
    var incoming = JsonSerializer.Deserialize<List<PackBuilder.Entry>>(File.ReadAllText(args[2])) ?? new List<PackBuilder.Entry>();
    int added = 0;
    foreach (var entry in incoming.Where(e => !string.IsNullOrEmpty(e.t)))
    {
        string? problem = PackBuilder.Check(entry.s, entry.t);
        if (problem != null) { Console.Error.WriteLine($"{problem}: {entry.s.Replace("\n", "\\n")}"); return 1; }
        table[entry.s] = entry.t;
        added++;
    }
    PackBuilder.SaveTranslations(root, "en", table);
    Console.WriteLine($"добавлено или заменено переводов: {added}, всего {table.Count}");
    return 0;
}

Console.Error.WriteLine("Неизвестная команда: " + command);
return 2;

/// <summary>Обход исходников одной программы и сбор её русских строк.</summary>
public static class Extractor
{
    public static readonly string[] Scopes = { "client", "launcher" };

    public static Collector Collect(string root, string scope)
    {
        string shared = Path.Combine(root, "Shared");
        string[] folders = scope == "client"
            ? new[] { Path.Combine(root, "Ven4Tools"), shared }
            : new[] { Path.Combine(root, "Ven4Tools.Launcher"), shared };

        var found = new Collector(root);
        foreach (string folder in folders)
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/obj/") || relative.Contains("/bin/") || relative.EndsWith(".g.cs")) continue;
                if (file.EndsWith(".cs")) CSharpSource.Collect(file, found);
                else if (file.EndsWith(".xaml")) XamlSource.Collect(file, found);
            }
        }
        if (scope == "client") CatalogSource.Collect(Path.Combine(root, "Catalog", "master.json"), found);
        return found;
    }
}

/// <summary>Сборка языкового пакета программы из общей таблицы переводов.</summary>
public static class PackBuilder
{
    public sealed record Entry(string s, string t);

    public static void SaveTranslations(string root, string language, Dictionary<string, string> table)
    {
        var entries = table.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new Entry(p.Key, p.Value));
        string json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(Path.Combine(root, "Localization", language + ".json"), json.Replace("\r\n", "\n") + "\n", new UTF8Encoding(false));
    }

    /// <summary>
    /// Перевод обязан сохранить то, на что опирается подстановка при показе: те же места
    /// {0}, {1}, столько же строк и те же пробелы по краям. null — всё в порядке.
    /// </summary>
    public static string? Check(string source, string target)
    {
        static IEnumerable<string> Holes(string text) =>
            Regex.Matches(text, @"(?<!\{)\{(\d+)\}(?!\})").Select(m => m.Groups[1].Value).Distinct().OrderBy(h => h, StringComparer.Ordinal);

        if (Holes(target).Except(Holes(source)).Any()) return "в переводе есть подстановка, которой нет в исходной строке";
        if (source.Count(c => c == '\n') != target.Count(c => c == '\n')) return "в переводе другое число строк";
        if (source.Length - source.TrimStart().Length != target.Length - target.TrimStart().Length
            || source.Length - source.TrimEnd().Length != target.Length - target.TrimEnd().Length) return "в переводе другие пробелы по краям";
        return null;
    }

    public static Dictionary<string, string> LoadTranslations(string root, string language)
    {
        string path = Path.Combine(root, "Localization", language + ".json");
        var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? new List<Entry>();
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries) table[entry.s] = entry.t;
        return table;
    }

    /// <summary>Строки, которые есть в коде, но показываются не человеку (слова поиска, шаблоны разбора).</summary>
    public static HashSet<string> LoadIgnored(string root)
    {
        string path = Path.Combine(root, "Localization", "ignore.json");
        return File.Exists(path)
            ? new HashSet<string>(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new List<string>(), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    public static (string Json, List<string> Missing) Build(string root, string scope, string language)
    {
        var translations = LoadTranslations(root, language);
        var ignored = LoadIgnored(root);
        var found = Extractor.Collect(root, scope);
        var literals = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var patterns = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (string source in found.Literals.Keys.Where(k => !ignored.Contains(k)))
        {
            if (translations.TryGetValue(source, out string? target)) { if (target != source) literals[source] = target; AddLines(source, target, literals, patterns); }
            else missing.Add(source);
        }
        foreach (string source in found.Formats.Keys.Where(k => !ignored.Contains(k)))
        {
            if (translations.TryGetValue(source, out string? target)) { patterns[source] = target; AddLines(source, target, literals, patterns); }
            else missing.Add(source);
        }

        var pack = new
        {
            format = 1,
            lang = language,
            literals,
            patterns = patterns.Select(p => new[] { p.Key, p.Value }),
        };
        string json = JsonSerializer.Serialize(pack, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return (json.Replace("\r\n", "\n"), missing);
    }

    // Сообщение из нескольких строк в коде часто собирается из кусков, и целиком в пакете
    // его не найти. Поэтому каждая строка многострочного текста попадает в пакет ещё и
    // сама по себе — программа, не найдя текст целиком, переводит его построчно.
    private static void AddLines(string source, string target, IDictionary<string, string> literals, IDictionary<string, string> patterns)
    {
        if (!source.Contains('\n')) return;
        string[] sourceLines = source.Split('\n');
        string[] targetLines = target.Split('\n');
        if (sourceLines.Length != targetLines.Length) return;
        for (int i = 0; i < sourceLines.Length; i++)
        {
            string s = sourceLines[i].Trim(), t = targetLines[i].Trim();
            if (s.Length == 0 || !Collector.HasCyrillic(s) || s == t) continue;
            bool isPattern = System.Text.RegularExpressions.Regex.IsMatch(s, @"(?<!\{)\{\d+\}(?!\})");
            var destination = isPattern ? patterns : literals;
            destination.TryAdd(s, t);
        }
    }
}

public sealed class Collector(string root)
{
    public Dictionary<string, List<string>> Literals { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> Formats { get; } = new(StringComparer.Ordinal);

    public static bool HasCyrillic(string text) => text.Any(c => c is >= 'А' and <= 'я' or 'ё' or 'Ё');

    public void Add(string text, bool isFormat, string file, int line)
    {
        if (string.IsNullOrWhiteSpace(text) || !HasCyrillic(text)) return;
        var target = isFormat ? Formats : Literals;
        if (!target.TryGetValue(text, out var where)) target[text] = where = new List<string>();
        where.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{line}");
    }
}

static class CSharpSource
{
    public static void Collect(string file, Collector found)
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            // Склейка "..." + x + "..." разбирается целиком от самого внешнего «+»:
            // на экран она попадает одной строкой, и шаблон у неё один.
            if (node is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } add)
            {
                if (add.Parent is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression }) continue;
                if (!IsStringConcat(add)) continue;
                Emit(Flatten(add), file, node, found);
                continue;
            }
            if (node is InterpolatedStringExpressionSyntax or LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression })
            {
                if (IsInsideConcat(node)) continue;
                Emit(Parts((ExpressionSyntax)node), file, node, found);
            }
        }
    }

    private static bool IsInsideConcat(SyntaxNode node)
    {
        var parent = node.Parent;
        while (parent is ParenthesizedExpressionSyntax) parent = parent.Parent;
        return parent is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } add && IsStringConcat(Outermost(add));
    }

    private static BinaryExpressionSyntax Outermost(BinaryExpressionSyntax add)
    {
        while (add.Parent is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } parent) add = parent;
        return add;
    }

    private static bool IsStringConcat(BinaryExpressionSyntax add) =>
        add.DescendantNodesAndSelf().Any(n =>
            n is InterpolatedStringExpressionSyntax or LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression });

    // Куски строки: текст (string) или подстановка (null).
    private static List<string?> Flatten(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax p) expression = p.Expression;
        if (expression is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } add)
        {
            var left = Flatten(add.Left);
            left.AddRange(Flatten(add.Right));
            return left;
        }
        return Parts(expression);
    }

    private static List<string?> Parts(ExpressionSyntax expression)
    {
        var parts = new List<string?>();
        switch (expression)
        {
            case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal:
                parts.Add(literal.Token.ValueText);
                break;
            case InterpolatedStringExpressionSyntax interpolated:
                foreach (var content in interpolated.Contents)
                {
                    if (content is InterpolatedStringTextSyntax text) parts.Add(text.TextToken.ValueText);
                    else parts.Add(null);
                }
                break;
            // Выбор из двух готовых строк по условию: каждая попадёт в пакет сама по себе
            // (её найдёт обход дерева), а в шаблоне на этом месте — подстановка.
            default:
                parts.Add(null);
                break;
        }
        return parts;
    }

    private static void Emit(List<string?> parts, string file, SyntaxNode node, Collector found)
    {
        var text = new StringBuilder();
        int holes = 0;
        bool lastWasHole = false;
        foreach (string? part in parts)
        {
            if (part == null)
            {
                // Две подстановки подряд на экране неразличимы — это одно место.
                if (!lastWasHole) text.Append('{').Append(holes++).Append('}');
                lastWasHole = true;
            }
            else if (part.Length > 0)
            {
                text.Append(part.Replace("{", "{{").Replace("}", "}}"));
                lastWasHole = false;
            }
        }
        int line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        if (holes == 0) found.Add(text.ToString().Replace("{{", "{").Replace("}}", "}"), false, file, line);
        else found.Add(text.ToString(), true, file, line);
    }
}

static partial class XamlSource
{
    [GeneratedRegex(@"'((?:[^'\\]|\\.)*)'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"\{(\d+)(?:[:,][^}]*)?\}")]
    private static partial Regex Hole();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public static void Collect(string file, Collector found)
    {
        XDocument document;
        try { document = XDocument.Load(file, LoadOptions.SetLineInfo); }
        catch (XmlException) { return; }

        foreach (var element in document.Descendants())
        {
            int line = ((IXmlLineInfo)element).LineNumber;
            foreach (var attribute in element.Attributes())
            {
                string name = attribute.Name.LocalName;
                if (attribute.IsNamespaceDeclaration || name is "Name" or "Key" or "Class" or "AutomationId" or "Uid") continue;
                string value = attribute.Value;
                if (!Collector.HasCyrillic(value)) continue;

                if (value.StartsWith('{') && !value.StartsWith("{}"))
                {
                    // Расширение разметки: текст внутри — в одинарных кавычках
                    // (StringFormat='Найдено: {0}', FallbackValue='…', ConverterParameter='…').
                    foreach (Match quoted in Quoted().Matches(value))
                        AddText(quoted.Groups[1].Value.Replace("\\'", "'"), file, line, found);
                }
                else AddText(value, file, line, found);
            }

            // Текст между тегами: WPF показывает его со схлопнутыми пробелами.
            foreach (var text in element.Nodes().OfType<XText>())
            {
                string normalized = Spaces().Replace(text.Value, " ").Trim();
                if (Collector.HasCyrillic(normalized))
                    found.Add(normalized, false, file, ((IXmlLineInfo)text).LineNumber);
            }
        }
    }

    private static void AddText(string value, string file, int line, Collector found)
    {
        if (value.StartsWith("{}")) value = value[2..];     // экранирование StringFormat="{}{0} шт."
        if (Hole().IsMatch(value))
        {
            // Формат привязки: {0:N1} → {0}; остальной текст — как в шаблонах из кода.
            string template = Hole().Replace(value.Replace("{{", "\u0001").Replace("}}", "\u0002"), m => "\u0003" + m.Groups[1].Value + "\u0004")
                .Replace("{", "{{").Replace("}", "}}")
                .Replace("\u0001", "{{").Replace("\u0002", "}}").Replace("\u0003", "{").Replace("\u0004", "}");
            found.Add(template, true, file, line);
        }
        else found.Add(value, false, file, line);
    }
}

static class CatalogSource
{
    // Каталог приходит с сервера отдельно от программы, но его текущие названия категорий
    // и описания программ тоже показываются на экране — они идут в пакет как обычные строки.
    public static void Collect(string file, Collector found)
    {
        if (!File.Exists(file)) return;
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        if (!document.RootElement.TryGetProperty("apps", out var apps)) return;
        foreach (var app in apps.EnumerateArray())
        {
            foreach (string field in new[] { "description", "category", "name" })
            {
                if (app.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
                    found.Add(value.GetString()!, false, file, 0);
            }
        }
    }
}
