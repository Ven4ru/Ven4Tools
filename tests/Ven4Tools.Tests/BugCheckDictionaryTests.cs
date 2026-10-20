using Ven4Tools.Services.Diagnostics;

namespace Ven4Tools.Tests;

public sealed class BugCheckDictionaryTests
{
    [Fact]
    public void Embedded_ЗагружаетсяИзРесурса_ОтСорокаКодов()
    {
        var dictionary = BugCheckDictionary.Embedded;
        Assert.InRange(dictionary.Count, 40, 200);
    }

    [Theory]
    [InlineData(0x0000000Au, "IRQL_NOT_LESS_OR_EQUAL")]
    [InlineData(0x000000D1u, "DRIVER_IRQL_NOT_LESS_OR_EQUAL")]
    [InlineData(0x00000133u, "DPC_WATCHDOG_VIOLATION")]
    [InlineData(0x00000124u, "WHEA_UNCORRECTABLE_ERROR")]
    [InlineData(0x0000009Fu, "DRIVER_POWER_STATE_FAILURE")]
    [InlineData(0xC000021Au, "STATUS_SYSTEM_PROCESS_TERMINATED")]
    public void Lookup_ЧастыеКоды_ИмяMicrosoft(uint code, string expectedName)
    {
        var description = BugCheckDictionary.Embedded.Lookup(code);
        Assert.NotNull(description);
        Assert.Equal(expectedName, description!.Name);
        Assert.False(string.IsNullOrWhiteSpace(description.CauseRu));
        Assert.False(string.IsNullOrWhiteSpace(description.CheckRu));
        Assert.False(string.IsNullOrWhiteSpace(description.CauseEn));
        Assert.False(string.IsNullOrWhiteSpace(description.CheckEn));
    }

    [Fact]
    public void Lookup_КодСБитом0x10000000_НаходитБазовыйКод()
    {
        // 0x1000007E — тот же SYSTEM_THREAD_EXCEPTION_NOT_HANDLED, что и 0x7E.
        var description = BugCheckDictionary.Embedded.Lookup(0x1000007Eu);
        Assert.NotNull(description);
        Assert.Equal("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", description!.Name);
    }

    [Fact]
    public void Lookup_НеизвестныйКод_Null()
    {
        Assert.Null(BugCheckDictionary.Embedded.Lookup(0x0000ABCDu));
    }

    [Fact]
    public void Embedded_УКаждойЗаписиРусскийИАнглийскийТекст_АнглийскийБезКириллицы()
    {
        // Английский текст словаря идёт на экран мимо языкового пакета — кириллица в нём
        // означала бы непереведённую строку в английском интерфейсе.
        string json = ReadEmbeddedJson();
        var dictionary = BugCheckDictionary.Parse(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        int total = doc.RootElement.GetProperty("codes").GetArrayLength();
        Assert.Equal(total, dictionary.Count); // ни одна запись не отброшена разбором

        foreach (var item in doc.RootElement.GetProperty("codes").EnumerateArray())
        {
            Assert.True(BugCheckDictionary.TryParseHex(item.GetProperty("code").GetString()!, out uint code));
            var d = dictionary.Lookup(code)!;
            Assert.True(HasCyrillic(d.CauseRu), d.Name);
            Assert.False(HasCyrillic(d.CauseEn), d.Name);
            Assert.False(HasCyrillic(d.CheckEn), d.Name);
            Assert.Matches("^[A-Z0-9_]+$", d.Name);
        }
    }

    [Fact]
    public void Parse_ЗаписиБезПолейПропускаются_ОстальныеРаботают()
    {
        const string json = """
        {
          "codes": [
            { "code": "0x0000000A", "name": "IRQL_NOT_LESS_OR_EQUAL",
              "ru": { "cause": "причина", "check": "проверить" }, "en": { "cause": "cause", "check": "check" } },
            { "code": "не код", "name": "BROKEN", "ru": { "cause": "a", "check": "b" }, "en": { "cause": "a", "check": "b" } },
            { "code": "0x50", "name": "NO_TEXT" },
            { "code": "0x1A", "name": "MEMORY_MANAGEMENT", "ru": { "cause": "a" }, "en": { "cause": "a", "check": "b" } }
          ]
        }
        """;
        var dictionary = BugCheckDictionary.Parse(json);
        Assert.Equal(1, dictionary.Count);
        Assert.NotNull(dictionary.Lookup(0xA));
        Assert.Null(dictionary.Lookup(0x50));
    }

    [Fact]
    public void Parse_БезМассиваCodes_ПустойСловарь()
    {
        Assert.Equal(0, BugCheckDictionary.Parse("{}").Count);
    }

    [Theory]
    [InlineData("0x0000000A", 0xAu)]
    [InlineData("0xa", 0xAu)]
    [InlineData("C000021A", 0xC000021Au)]
    public void TryParseHex_РазныеЗаписи(string text, uint expected)
    {
        Assert.True(BugCheckDictionary.TryParseHex(text, out uint value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void FormatCode_ВосемьЗнаковСПрефиксом()
    {
        Assert.Equal("0x0000000A", BugCheckDictionary.FormatCode(0xA));
        Assert.Equal("0xC000021A", BugCheckDictionary.FormatCode(0xC000021A));
    }

    [Fact]
    public void MicrosoftInfoUrl_ВедётВДокументациюMicrosoftСКодом()
    {
        string url = BugCheckDictionary.MicrosoftInfoUrl(0x133, "DPC_WATCHDOG_VIOLATION");
        Assert.StartsWith("https://learn.microsoft.com/", url);
        Assert.Contains("0x133", url);
        Assert.Contains("DPC_WATCHDOG_VIOLATION", url);

        string unknown = BugCheckDictionary.MicrosoftInfoUrl(0xABCD, null);
        Assert.StartsWith("https://learn.microsoft.com/", unknown);
        Assert.Contains("0xABCD", unknown);
    }

    private static bool HasCyrillic(string text) => text.Any(c => c is >= 'А' and <= 'я' or 'ё' or 'Ё');

    private static string ReadEmbeddedJson()
    {
        var asm = typeof(BugCheckDictionary).Assembly;
        using var stream = asm.GetManifestResourceStream(BugCheckDictionary.ResourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
