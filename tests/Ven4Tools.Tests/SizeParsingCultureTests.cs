using System.Globalization;
using Ven4Tools.Services;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

/// <summary>
/// Разбор размеров установщиков не должен зависеть от языка системы.
/// <para>Каталог (<c>master.json</c>) и вывод winget записывают дробную часть через
/// точку, а на русской локали — основной для проекта — точка не является ни
/// десятичным разделителем, ни разделителем групп (там NBSP). Разбор по текущей
/// культуре молча проваливался, и размер подменялся заглушкой 100 МБ: у каталога
/// это касалось 62 записей из 71.</para>
/// </summary>
public sealed class SizeParsingCultureTests
{
    /// <summary>Выполняет проверку под заданной культурой и возвращает культуру потока обратно.</summary>
    private static void WithCulture(string cultureName, Action assertions)
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
        try { assertions(); }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    [InlineData("")]
    public void ParseSizeToMB_НеЗависитОтКультуры(string culture)
    {
        WithCulture(culture, () =>
        {
            Assert.Equal(84, CatalogViewModel.ParseSizeToMB("84.7 MB"));
            Assert.Equal(131, CatalogViewModel.ParseSizeToMB("131.8 MB"));
            Assert.Equal(210, CatalogViewModel.ParseSizeToMB("~210 MB"));
            Assert.Equal(1228, CatalogViewModel.ParseSizeToMB("1.2 GB"));
        });
    }

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("en-US")]
    public void ParseSizeToMB_ПустаяСтрокаДаётЗаглушку(string culture)
    {
        // 100 — та же заглушка, что и раньше; важно, что до неё доходят только
        // действительно неразбираемые значения, а не любой дробный размер.
        WithCulture(culture, () => Assert.Equal(100, CatalogViewModel.ParseSizeToMB("")));
    }

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    public void ParseWingetSize_НеЗависитОтКультуры(string culture)
    {
        WithCulture(culture, () =>
        {
            using var checker = new AvailabilityChecker();
            Assert.Equal(84, checker.ParseWingetSize("Installer Size: 84.7 MB"));
            // Winget на локализованной системе печатает размер с запятой —
            // нормализация к точке обязана работать в обе стороны.
            Assert.Equal(84, checker.ParseWingetSize("Installer Size: 84,7 MB"));
            Assert.Equal(2048, checker.ParseWingetSize("Installer Size: 2.0 GB"));
        });
    }
}
