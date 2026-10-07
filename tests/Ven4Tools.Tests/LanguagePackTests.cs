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

    [Theory]
    [InlineData("• Готово", "• Done")]
    [InlineData("⚠ Готово", "⚠ Done")]
    [InlineData("— 512 МБ", "— 512 MB")]
    [InlineData("🔍 Статус: Установлено", "🔍 Status: Installed")]
    public void MarkerBeforeText_DoesNotHideTranslation(string source, string expected)
    {
        Assert.Equal(expected, Sample.Translate(source));
    }

    [Fact]
    public void MarkedLines_AreTranslatedLineByLine()
    {
        Assert.Equal("• Done\n• Installed", Sample.Translate("• Готово\n• Установлено"));
    }

    [Theory]
    [InlineData("12,7 МБ", "12.7 MB")]          // дробное число: в английском тексте — точка
    [InlineData("1024 МБ", "1024 MB")]
    [InlineData("1,2,3 МБ", "1,2,3 MB")]        // перечисление — не трогаем
    [InlineData("1,234 МБ", "1,234 MB")]        // три знака после запятой — не дробь
    public void DecimalComma_BecomesPointInEnglish(string source, string expected)
    {
        Assert.Equal(expected, Sample.Translate(source));
    }

    [Theory]
    [InlineData("12,7 MB", "12.7 MB")]                      // размер из каталога: русского текста нет вовсе
    [InlineData("≈ 4,5 GB", "≈ 4.5 GB")]
    [InlineData("512,5 MB/s", "512.5 MB/s")]
    [InlineData("0,4 ms", "0.4 ms")]
    [InlineData("12,5%", "12.5%")]
    [InlineData("Disk 0: Msft Virtual Disk — 103,1 GB", "Disk 0: Msft Virtual Disk — 103.1 GB")]
    [InlineData("Firefox, Chrome, Edge", "Firefox, Chrome, Edge")]   // перечисление
    [InlineData("1,234,567 B", "1,234,567 B")]                      // разряды, не дробь
    [InlineData("v1,5 Beta", "v1,5 Beta")]                          // «B» — начало слова, не единица
    public void PlainSizes_GetDecimalPointInEnglish(string source, string expected)
    {
        Assert.Equal(expected, Sample.Translate(source));
    }

    [Fact]
    public void LeadingIndent_AndGenericUnitPattern_DoNotHideSpecificPattern()
    {
        // В коде строка с отступом и значком; общий шаблон «{0} ГБ» не должен перехватить её.
        var pack = Pack("""
            {"lang":"en","literals":{},
             "patterns":[["   ✅ Свободно ≈{0} ГБ","   ✅ Free: ≈{0} GB"],["{0} ГБ","{0} GB"]]}
            """);
        Assert.Equal("   ✅ Free: ≈73 GB", pack.Translate("   ✅ Свободно ≈73 ГБ"));
        Assert.Equal("[10:00:00]    ✅ Free: ≈73 GB", pack.Translate("[10:00:00]    ✅ Свободно ≈73 ГБ"));
        Assert.Equal("120 GB", pack.Translate("120 ГБ"));
    }

    [Fact]
    public void DecimalComma_IsKeptForOtherLanguages()
    {
        var german = Pack("""{"lang":"de","literals":{},"patterns":[["{0} МБ","{0} MB"]]}""");
        Assert.Equal("12,7 MB", german.Translate("12,7 МБ"));
    }

    // Строки, собранные в коде из кусков: значок рисуется отдельно, к тексту дописано
    // многоточие, части соединены разделителем, впереди отметка времени журнала.
    private static readonly LanguagePack Composite = Pack("""
        {
          "lang": "en",
          "literals": {
            "✅ Готово": "✅ Done",
            "ℹ️ Уже установлено": "ℹ️ Already installed",
            "Список программ": "App list",
            "запись": "write",
            "исправен": "healthy",
            "Установлено": "Installed",
            "Скорость:": "Speed:"
          },
          "patterns": [
            ["✅ Версии загружены для {0} приложений", "✅ Versions loaded for {0} apps"],
            ["{0} МБ/с", "{0} MB/s"]
          ]
        }
        """);

    [Theory]
    [InlineData("Готово", "Done")]                                   // в коде строка со значком, на экране — без
    [InlineData("Уже установлено", "Already installed")]             // «ℹ» по Юникоду буква, но это значок
    [InlineData("Версии загружены для 80 приложений", "Versions loaded for 80 apps")]
    [InlineData("⏳ Список программ…", "⏳ App list…")]               // значок и многоточие дописаны в коде
    [InlineData("Список программ...", "App list...")]
    [InlineData("RND4K Q1T1 — запись", "RND4K Q1T1 — write")]        // части через разделитель
    [InlineData("🟢 Msft Virtual Disk — исправен", "🟢 Msft Virtual Disk — healthy")]
    [InlineData("Установлено: AutoHotkey", "Installed: AutoHotkey")]
    [InlineData("Скорость: 512,5 МБ/с", "Speed: 512.5 MB/s")]        // «Слово:» записано в пакете с двоеточием
    [InlineData("[12:34:56] ✅ Готово", "[12:34:56] ✅ Done")]         // отметка времени журнала
    [InlineData("[12:34:56] Версии загружены для 3 приложений", "[12:34:56] Versions loaded for 3 apps")]
    public void TextAssembledInCode_IsStillTranslated(string source, string expected)
    {
        Assert.Equal(expected, Composite.Translate(source));
    }

    [Fact]
    public void BracketWithText_IsPartOfTheString_NotAMark()
    {
        // «[Сеть] …» — метка раздела, часть самой строки: без записи в пакете она не переводится.
        Assert.Equal("[Сеть] Готово к работе", Composite.Translate("[Сеть] Готово к работе"));
    }

    [Fact]
    public void PartlyKnownParts_AreTranslated_AndTheRestIsReported()
    {
        var missed = new List<string>();
        var pack = Pack("""{"lang":"en","literals":{"Установлено":"Installed"},"patterns":[]}""");
        pack.Missed = missed.Add;

        Assert.Equal("Installed: МояПрограмма", pack.Translate("Установлено: МояПрограмма"));
        Assert.Single(missed);
    }

    [Fact]
    public void GrowingLog_IsTranslatedLineByLine_AndReportsOnlyUnknownLines()
    {
        var missed = new List<string>();
        var pack = Pack("""{"lang":"en","literals":{"Готово":"Done"},"patterns":[["Шаг {0}","Step {0}"]]}""");
        pack.Missed = missed.Add;

        var log = new StringBuilder();
        for (int i = 1; i <= 300; i++) log.Append("[10:00:00] Шаг ").Append(i).Append('\n');
        log.Append("[10:00:01] Неизвестная строка\n[10:00:02] Готово\n");

        string translated = pack.Translate(log.ToString());

        Assert.StartsWith("[10:00:00] Step 1\n[10:00:00] Step 2\n", translated);
        Assert.EndsWith("[10:00:01] Неизвестная строка\n[10:00:02] Done\n", translated);
        Assert.Equal(new[] { "[10:00:01] Неизвестная строка" }, missed);
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
