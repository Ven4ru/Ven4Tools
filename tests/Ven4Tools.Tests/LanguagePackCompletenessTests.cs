extern alias client;

using System.Text;
using client::Ven4Tools.Localization;

namespace Ven4Tools.Tests;

/// <summary>
/// Английский перевод не отстаёт от программы. Русский текст в коде — исходник; каждая
/// новая или изменённая русская строка должна получить перевод в Localization/en.json,
/// а языковые пакеты Localization/packs/*.json — быть пересобраны
/// (dotnet run --project Tools/LanguagePackTool -- pack .). Контрольная сумма пакета вшита в
/// программу, поэтому устаревший пакет — это программа без перевода у пользователя.
/// </summary>
public class LanguagePackCompletenessTests
{
    private static readonly string Root = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ven4Tools.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Не найден корень репозитория (Ven4Tools.sln).");
    }

    [Theory]
    [InlineData("client")]
    [InlineData("launcher")]
    public void EveryRussianString_HasTranslation(string scope)
    {
        var (_, missing) = PackBuilder.Build(Root, scope, "en");
        Assert.True(missing.Count == 0,
            $"Без перевода строк: {missing.Count}. Добавьте их в Localization/en.json " +
            "(заготовка: dotnet run --project Tools/LanguagePackTool -- missing . missing.json; " +
            "внести: ... -- merge . missing.json), затем пересоберите пакеты: ... -- pack .\n" +
            string.Join("\n", missing.Take(15).Select(s => "  " + s.Replace("\n", "\\n"))));
    }

    [Theory]
    [InlineData("client")]
    [InlineData("launcher")]
    public void CommittedPack_MatchesSources(string scope)
    {
        var (json, _) = PackBuilder.Build(Root, scope, "en");
        byte[] committed = File.ReadAllBytes(Path.Combine(Root, "Localization", "packs", $"{scope}-en.json"));
        Assert.True(new UTF8Encoding(false).GetBytes(json).AsSpan().SequenceEqual(committed),
            $"Пакет Localization/packs/{scope}-en.json устарел. Пересоберите: dotnet run --project Tools/LanguagePackTool -- pack .");
    }

    [Theory]
    [InlineData("client")]
    [InlineData("launcher")]
    public void CommittedPack_IsReadableByTheProgram(string scope)
    {
        var pack = LanguagePack.Parse(File.ReadAllBytes(Path.Combine(Root, "Localization", "packs", $"{scope}-en.json")));
        Assert.Equal("en", pack.Language);
        Assert.True(pack.LiteralCount > 100);

        // Каждый шаблон из таблицы обязан превратиться в рабочий: иначе строка с
        // подстановкой останется русской, а сборка пакета этого не заметит.
        using var document = System.Text.Json.JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(Root, "Localization", "packs", $"{scope}-en.json")));
        Assert.Equal(document.RootElement.GetProperty("patterns").GetArrayLength(), pack.PatternCount);
    }

    [Fact]
    public void Translations_KeepPlaceholdersLinesAndEdges()
    {
        var problems = PackBuilder.LoadTranslations(Root, "en")
            .Select(pair => (pair.Key, Problem: PackBuilder.Check(pair.Key, pair.Value)))
            .Where(x => x.Problem != null)
            .Select(x => $"  {x.Problem}: {x.Key.Replace("\n", "\\n")}")
            .ToList();
        Assert.True(problems.Count == 0, "Переводы с нарушением формата:\n" + string.Join("\n", problems.Take(15)));
    }
}
