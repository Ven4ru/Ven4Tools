extern alias client;

using System.Security.Cryptography;
using System.Text;
using client::Ven4Tools.Localization;

namespace Ven4Tools.Tests;

/// <summary>
/// Языковой пакет: перевод строк интерфейса при показе. Типы перевода общие для клиента
/// и лаунчера (один исходник в двух сборках), поэтому здесь они берутся из клиента явно.
/// </summary>
public class LanguagePackTests
{
    private static LanguagePack Pack(string json) => LanguagePack.Parse(Encoding.UTF8.GetBytes(json));

    private static readonly LanguagePack Sample = Pack("""
        {
          "format": 1,
          "lang": "en",
          "literals": {
            "Готово": "Done",
            "Установлено": "Installed",
            "Первая строка": "First line",
            "Вторая строка": "Second line",
            "Скобки {как есть}": "Braces {as is}"
          },
          "patterns": [
            ["{0} МБ", "{0} MB"],
            ["{0} МБ свободно", "{0} MB free"],
            ["Статус: {0}", "Status: {0}"],
            ["Ошибок: {0} из {1}", "{1} total, {0} failed"],
            ["Код {{{0}}}", "Code {{{0}}}"]
          ]
        }
        """);

    [Fact]
    public void Literal_IsTranslatedExactly()
    {
        Assert.Equal("en", Sample.Language);
        Assert.Equal("Done", Sample.Translate("Готово"));
    }

    [Fact]
    public void TextWithoutRussian_IsReturnedAsIs()
    {
        Assert.Equal("Ven4Tools 6.1.0", Sample.Translate("Ven4Tools 6.1.0"));
        Assert.Equal("", Sample.Translate(null));
        Assert.Equal("", Sample.Translate(""));
    }

    [Fact]
    public void SurroundingWhitespace_IsKept()
    {
        Assert.Equal("  Done\n", Sample.Translate("  Готово\n"));
    }

    [Fact]
    public void Pattern_CarriesValuesIntoTranslation()
    {
        Assert.Equal("512 MB", Sample.Translate("512 МБ"));
    }

    [Fact]
    public void MoreSpecificPattern_WinsOverGeneralOne()
    {
        Assert.Equal("512 MB free", Sample.Translate("512 МБ свободно"));
    }

    [Fact]
    public void Pattern_MayReorderValues()
    {
        Assert.Equal("7 total, 2 failed", Sample.Translate("Ошибок: 2 из 7"));
    }

    [Fact]
    public void ValueInsidePattern_IsTranslatedToo()
    {
        Assert.Equal("Status: Installed", Sample.Translate("Статус: Установлено"));
    }

    [Fact]
    public void EscapedBraces_AreLiteralBraces()
    {
        Assert.Equal("Code {42}", Sample.Translate("Код {42}"));
        Assert.Equal("Braces {as is}", Sample.Translate("Скобки {как есть}"));
    }

    [Fact]
    public void MultilineText_FallsBackToLineByLine()
    {
        Assert.Equal("First line\n  Second line\n512 MB", Sample.Translate("Первая строка\n  Вторая строка\n512 МБ"));
    }

    [Fact]
    public void UnknownRussianText_StaysAndIsReported()
    {
        var pack = Pack("""{"lang":"en","literals":{"Да":"Yes"},"patterns":[]}""");
        var missed = new List<string>();
        pack.Missed = missed.Add;

        Assert.Equal("Неизвестная строка", pack.Translate("Неизвестная строка"));
        Assert.Equal("Yes", pack.Translate("Да"));
        Assert.Equal(new[] { "Неизвестная строка" }, missed);
    }

    [Fact]
    public void PartlyTranslatedMultilineText_IsReportedAsMissed()
    {
        var missed = new List<string>();
        var pack = Pack("""{"lang":"en","literals":{"Готово":"Done"},"patterns":[]}""");
        pack.Missed = missed.Add;

        Assert.Equal("Done\nНе готово", pack.Translate("Готово\nНе готово"));
        Assert.Single(missed);
    }

    [Fact]
    public void PatternWithUnknownHoleInTranslation_IsDropped()
    {
        // В переводе подстановка {1}, которой нет в исходной строке: подставить нечего.
        var pack = Pack("""{"lang":"en","literals":{},"patterns":[["Всего {0}","Total {1}"]]}""");
        Assert.Equal(0, pack.PatternCount);
        Assert.Equal("Всего 5", pack.Translate("Всего 5"));
    }

    [Fact]
    public void BrokenFile_Throws()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Pack("{ это не json"));
    }

    // ── Хранилище: принимается только пакет этой сборки ──────────────────────────

    [Fact]
    public void Store_LoadsSeedPack_OnlyWhenChecksumMatches()
    {
        string dir = Path.Combine(Path.GetTempPath(), "v4t-lang-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "lang"));
            byte[] bytes = Encoding.UTF8.GetBytes("""{"lang":"en","literals":{"Да":"Yes"},"patterns":[]}""");
            File.WriteAllBytes(Path.Combine(dir, "lang", "en.json"), bytes);
            string sha = Convert.ToHexString(SHA256.HashData(bytes));

            // Несуществующая программа — чтобы в профиле пользователя точно не было её пакета.
            string component = "test-" + Guid.NewGuid().ToString("N");
            var log = new List<string>();

            var good = LanguagePackStore.TryLoadLocal(
                new LanguagePackSource(component, "en", sha, Array.Empty<string>()), dir, log.Add);
            Assert.NotNull(good);
            Assert.Equal("Yes", good!.Translate("Да"));

            var foreign = LanguagePackStore.TryLoadLocal(
                new LanguagePackSource(component, "en", new string('0', 64), Array.Empty<string>()), dir, log.Add);
            Assert.Null(foreign);
            Assert.Contains(log, line => line.Contains("не от этой сборки"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Store_DefaultUrls_PointToCdnMirrorAndGitHub()
    {
        var client = LanguagePackStore.DefaultUrls("client", "en", "6.1.0");
        Assert.Equal(new[]
        {
            "https://cdn.ven4tools.ru/releases/lang/client-6.1.0-en.json",
            "https://ven4tools.ru/releases/lang/client-6.1.0-en.json",
            "https://github.com/Ven4ru/Ven4Tools/releases/download/v6.1.0/Ven4Tools-Client-6.1.0-lang-en.json",
        }, client);

        var launcher = LanguagePackStore.DefaultUrls("launcher", "en", "3.10.0");
        Assert.Equal(
            "https://github.com/Ven4ru/Ven4Tools/releases/download/launcher-v3.10.0/Ven4Tools-Launcher-3.10.0-lang-en.json",
            launcher[2]);
    }

    [Theory]
    [InlineData("ru", true)]
    [InlineData("EN", true)]
    [InlineData("auto", false)]
    [InlineData("de", false)]
    [InlineData(null, false)]
    public void AppLanguage_SupportsOnlyRussianAndEnglish(string? language, bool supported)
    {
        Assert.Equal(supported, AppLanguage.IsSupported(language));
    }
}
