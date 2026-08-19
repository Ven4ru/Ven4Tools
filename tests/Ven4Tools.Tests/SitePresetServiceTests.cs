using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Разбор кода набора, вставленного из буфера обмена. Сети здесь нет
/// вообще: код самодостаточен, сервер о наборах ничего не знает.
/// </summary>
public class SitePresetServiceTests
{
    [Fact]
    public void Parse_ОбычныйКодДаётСписокПриложений()
    {
        var result = SitePresetService.Parse("V4T:google-chrome,telegram,vlc");

        Assert.True(result.Success);
        Assert.Equal(new[] { "google-chrome", "telegram", "vlc" }, result.AppIds);
    }

    [Theory]
    [InlineData("v4t:google-chrome")]                    // нижний регистр префикса
    [InlineData("  V4T:google-chrome  ")]                // лишние пробелы по краям
    [InlineData("V4T: google-chrome")]                   // пробел после префикса
    [InlineData("V4T:google-chrome,")]                   // висящая запятая
    [InlineData("V4T:google-chrome;google-chrome")]      // повтор того же id
    public void Parse_ТерпитТоЧтоПриезжаетИзБуфера(string raw)
    {
        var result = SitePresetService.Parse(raw);

        Assert.True(result.Success);
        Assert.Equal(new[] { "google-chrome" }, result.AppIds);
    }

    [Fact]
    public void Parse_ПринимаетСсылкуССайтаЦеликом()
    {
        // Человек копирует то, что попалось под руку: код или адресную строку.
        var result = SitePresetService.Parse("https://ven4tools.ru/?scene=catalog&set=google-chrome,vlc");

        Assert.True(result.Success);
        Assert.Equal(new[] { "google-chrome", "vlc" }, result.AppIds);
    }

    [Fact]
    public void Parse_ОтсекаетХвостПослеСпискаВСсылке()
    {
        var result = SitePresetService.Parse("https://ven4tools.ru/?set=vlc&scene=catalog#anchor");

        Assert.True(result.Success);
        Assert.Equal(new[] { "vlc" }, result.AppIds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_ПустойВводОтклоняетсяСПодсказкой(string? raw)
    {
        var result = SitePresetService.Parse(raw);

        Assert.False(result.Success);
        Assert.Contains("пуст", result.Error);
        Assert.Empty(result.AppIds);
    }

    [Theory]
    [InlineData("google-chrome,telegram")]   // без префикса и не ссылка
    [InlineData("просто текст")]
    [InlineData("V4T-6CRWK")]                // старый серверный формат больше не поддерживается
    public void Parse_ЧужаяСтрокаОтклоняется(string raw)
    {
        var result = SitePresetService.Parse(raw);

        Assert.False(result.Success);
        Assert.Empty(result.AppIds);
    }

    [Fact]
    public void Parse_КодБезЕдиногоГодногоIdОтклоняется()
    {
        // Из буфера может приехать что угодно; по этим значениям дальше идёт
        // поиск в каталоге, поэтому мусор не должен доезжать до него вовсе.
        var result = SitePresetService.Parse("V4T:!!!,@@@,###");

        Assert.False(result.Success);
        Assert.Contains("нет ни одного приложения", result.Error);
    }

    [Fact]
    public void Parse_ОтбрасываетНегодныеIdНоОставляетГодные()
    {
        var result = SitePresetService.Parse("V4T:google-chrome,пробел тут,vlc");

        Assert.True(result.Success);
        Assert.Equal(new[] { "google-chrome", "vlc" }, result.AppIds);
    }

    [Fact]
    public void Parse_НеТащитВКаталогСлишкомДлинныйId()
    {
        var longId = new string('a', 65);
        var result = SitePresetService.Parse($"V4T:{longId},vlc");

        Assert.True(result.Success);
        Assert.Equal(new[] { "vlc" }, result.AppIds);
    }
}
