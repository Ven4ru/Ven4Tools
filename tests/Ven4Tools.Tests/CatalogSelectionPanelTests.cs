using Ven4Tools.Helpers;
using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

/// <summary>
/// Панель «Ваш набор» нового каталога: размер из строки каталога, итог по набору
/// и код набора, которым можно поделиться.
/// </summary>
public class CatalogSelectionPanelTests
{
    [Theory]
    [InlineData("89.6 MB", 89.6)]
    [InlineData("~70 MB", 70)]
    [InlineData("1.5 GB", 1536)]
    [InlineData("512 KB", 0.5)]
    [InlineData(" 3,8 мб ", 3.8)]
    public void РазмерИзКаталога_РазбираетсяВМегабайты(string text, double expected)
    {
        Assert.True(CatalogSize.TryParseMegabytes(text, out double megabytes));
        Assert.Equal(expected, megabytes, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("зависит от версии")]
    [InlineData("MB")]
    public void НеразборчивыйРазмер_НеСчитаетсяНулём(string? text)
    {
        Assert.False(CatalogSize.TryParseMegabytes(text, out _));
        Assert.Equal("", CatalogSize.ToDisplay(text));
    }

    [Theory]
    [InlineData("89.6 MB", "89,6 МБ")]
    [InlineData("~70 MB", "≈ 70 МБ")]
    [InlineData("156.5 MB", "157 МБ")]
    [InlineData("1.5 GB", "1,5 ГБ")]
    public void РазмерПоказываетсяПоРусски(string text, string expected) =>
        Assert.Equal(expected, CatalogSize.ToDisplay(text));

    [Fact]
    public void ИтогНабора_СкладываетИзвестныеРазмеры() =>
        Assert.Equal("≈ 144 МБ",
            CatalogViewModel.DescribeTotal(new[] { "89.6 MB", "53.1 MB", "1.6 MB" }));

    [Fact]
    public void ИтогНабора_ОтдельноСчитаетПрограммыБезРазмера() =>
        Assert.Equal("≈ 89,6 МБ и 2 без размера",
            CatalogViewModel.DescribeTotal(new[] { "89.6 MB", "", null }));

    [Fact]
    public void ИтогНабора_БезЕдиногоРазмера_ГоворитОбЭтомСловами() =>
        Assert.Equal("размер неизвестен", CatalogViewModel.DescribeTotal(new string?[] { "", null }));

    [Fact]
    public void КодНабора_ЧитаетсяОбратноТемЖеРазбором()
    {
        string code = SitePresetService.BuildCode(new[] { "google-chrome", "telegram", "7zip" });

        Assert.Equal("V4T:google-chrome,telegram,7zip", code);
        var parsed = SitePresetService.Parse(code);
        Assert.True(parsed.Success);
        Assert.Equal(new[] { "google-chrome", "telegram", "7zip" }, parsed.AppIds);
    }

    [Fact]
    public void КодНабора_ПропускаетТоЧтоРазборВсёРавноОтбросит()
    {
        // Программа, добавленная вручную, может называться как угодно — в код набора
        // попадают только идентификаторы каталога, и повтор в нём не дублируется.
        string code = SitePresetService.BuildCode(new[] { "vlc", "Моя программа", "VLC", " " });

        Assert.Equal("V4T:vlc", code);
    }

    [Fact]
    public void КодНабора_ПустДляПустогоНабора() =>
        Assert.Equal("", SitePresetService.BuildCode(System.Array.Empty<string>()));

    private static AppRowViewModel Row(bool userAdded = false) =>
        new(new AppInfo { Id = "firefox", DisplayName = "Mozilla Firefox", IsUserAdded = userAdded });

    [Fact]
    public void Карточка_ПодНазваниемВерсияИРазмер()
    {
        var row = Row();
        row.CatalogVersion = "157.0";
        row.CatalogSizeText = "89.6 MB";

        Assert.Equal("157.0 · 89,6 МБ", row.CardMetaText);
        Assert.Equal("89,6 МБ", row.SetSizeText);
    }

    [Fact]
    public void Карточка_БезДанныхКаталога_НеВыдумываетИх()
    {
        Assert.Equal("", Row().CardMetaText);
        Assert.Equal("—", Row().SetSizeText);
        Assert.Equal("добавлено вручную", Row(userAdded: true).CardMetaText);
    }

    [Fact]
    public void Карточка_ПодписьПереключателяНазываетПричину()
    {
        var row = Row();
        Assert.Equal("Добавить", row.SelectLabel);

        row.IsSelected = true;
        Assert.Equal("✓ В наборе", row.SelectLabel);

        row.IsSelected = false;
        row.Availability = AppRowViewModel.RowAvailability.Unavailable;
        Assert.Equal("Недоступно", row.SelectLabel);

        row.Availability = AppRowViewModel.RowAvailability.Available;
        row.JustInstalled = true;
        Assert.Equal("Установлено", row.SelectLabel);
    }
}
