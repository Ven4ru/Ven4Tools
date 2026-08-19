using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Разбор кода набора, введённого человеком руками. Сетевую часть здесь
/// не трогаем — проверяем ровно то, что защищает от опечаток до запроса.
/// </summary>
public class SitePresetServiceTests
{
    [Theory]
    [InlineData("V4T-6CRWK", "6CRWK")]
    [InlineData("v4t-6crwk", "6CRWK")]
    [InlineData("6CRWK", "6CRWK")]
    [InlineData("  6crwk  ", "6CRWK")]
    [InlineData("V4T 6 CRWK", "6CRWK")]
    [InlineData("v4t-6-crwk", "6CRWK")]
    [InlineData("V4T6CRWK", "6CRWK")]
    public void NormalizeCode_ПриводитЛюбуюЗаписьКодаКОдномуВиду(string raw, string expected)
    {
        Assert.Equal(expected, SitePresetService.NormalizeCode(raw));
    }

    [Fact]
    public void NormalizeCode_КороткийКодНачинающийсяСV4TНеОбрезается()
    {
        // V, 4 и T входят в алфавит кода, поэтому «V4TQR» — это сам код,
        // а не префикс с остатком. Срезав префикс здесь, мы бы отправили
        // на сервер «QR» и получили «набор не найден» на верном коде.
        Assert.Equal("V4TQR", SitePresetService.NormalizeCode("V4TQR"));
        Assert.Equal("V4TQR", SitePresetService.NormalizeCode("v4t-V4TQR"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("V4T-")]
    public void NormalizeCode_ПустойВводДаётПустуюСтроку(string? raw)
    {
        Assert.Equal("", SitePresetService.NormalizeCode(raw));
    }

    [Theory]
    [InlineData("V4T-6CRWK")]
    [InlineData("6crwk")]
    [InlineData("V4T-T9EAN")]
    public void LooksLikeCode_НастоящийКодПринимается(string raw)
    {
        Assert.True(SitePresetService.LooksLikeCode(raw));
    }

    [Theory]
    // Символы, которых нет в алфавите сайта именно потому, что их путают
    // при переписывании от руки: 0/O, 1/I/L, 5/S, 8/B.
    [InlineData("V4T-0CRWK")]
    [InlineData("V4T-1CRWK")]
    [InlineData("V4T-5CRWK")]
    [InlineData("V4T-8CRWK")]
    [InlineData("V4T-OCRWK")]
    [InlineData("V4T-ICRWK")]
    [InlineData("V4T-LCRWK")]
    [InlineData("V4T-SCRWK")]
    [InlineData("V4T-BCRWK")]
    public void LooksLikeCode_СпорныеСимволыОтклоняются(string raw)
    {
        Assert.False(SitePresetService.LooksLikeCode(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("V4T")]
    [InlineData("ABC")]                    // короче четырёх знаков
    [InlineData("V4T-ABCDEFGHIJKLMNOP")]   // длиннее допустимого
    [InlineData("набор")]                  // кириллица
    [InlineData("V4T-@#$%^")]
    public void LooksLikeCode_МусорОтклоняется(string raw)
    {
        Assert.False(SitePresetService.LooksLikeCode(raw));
    }

    [Fact]
    public async Task FetchAsync_НекорректныйКодНеУходитВСеть()
    {
        // Ожидание: отказ приходит мгновенно и с объяснением, а не таймаутом.
        var result = await SitePresetService.FetchAsync("V4T-00000");

        Assert.False(result.Success);
        Assert.Contains("неправильно", result.Error);
        Assert.Empty(result.AppIds);
    }
}
