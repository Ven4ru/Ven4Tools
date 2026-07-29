using Ven4Tools.Models;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

/// <summary>
/// Локальный поиск по каталогу. До 2026-07-29 фильтр сравнивал только отображаемое
/// имя, поэтому осмысленные запросы вроде «архиватор» ничего не находили в
/// курируемом каталоге и пользователь уходил во внешний поиск winget/Chocolatey.
/// </summary>
public sealed class CatalogRowSearchTests
{
    // winget-идентификатор каталога попадает в AppInfo.AlternativeId (см.
    // CatalogViewModel.SyncCatalogToAppManager), отдельного поля WingetId у AppInfo нет.
    private static AppRowViewModel CreateSevenZipRow() =>
        new(new AppInfo
        {
            Id = "7zip",
            DisplayName = "7-Zip",
            Category = AppCategory.Системные,
            AlternativeId = "7zip.7zip",
            ChocoId = "7zip"
        })
        {
            Description = "Бесплатный архиватор с высокой степенью сжатия."
        };

    [Fact]
    public void ПоискПоФрагментуОписания_НаходитПриложение()
    {
        Assert.True(CreateSevenZipRow().MatchesSearch("архиватор"));
    }

    [Theory]
    [InlineData("7-Zip")]
    [InlineData("7-zip")]
    [InlineData("Zip")]
    public void ПоискПоИмени_РаботаетКакРаньше(string query)
    {
        Assert.True(CreateSevenZipRow().MatchesSearch(query));
    }

    [Fact]
    public void ПоискПоWingetId_НаходитПриложение()
    {
        Assert.True(CreateSevenZipRow().MatchesSearch("7zip.7zip"));
    }

    [Fact]
    public void ПоискПоChocoId_НаходитПриложение()
    {
        var row = new AppRowViewModel(new AppInfo
        {
            Id = "notepadplusplus",
            DisplayName = "Notepad++",
            Category = AppCategory.Разработка,
            ChocoId = "notepadplusplus"
        });

        Assert.True(row.MatchesSearch("notepadplus"));
    }

    [Fact]
    public void ПоискБезРегистра_НаходитПоОписаниюИИдентификаторам()
    {
        var row = CreateSevenZipRow();

        Assert.True(row.MatchesSearch("АРХИВАТОР"));
        Assert.True(row.MatchesSearch("7ZIP.7ZIP"));
    }

    [Fact]
    public void ПостороннийЗапрос_НеНаходитПриложение()
    {
        Assert.False(CreateSevenZipRow().MatchesSearch("браузер"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ПустойЗапрос_ПропускаетВсеСтроки(string? query)
    {
        Assert.True(CreateSevenZipRow().MatchesSearch(query));
    }

    // Пустое описание/идентификаторы не должны давать ложных совпадений: у
    // пользовательских приложений эти поля пусты по построению.
    [Fact]
    public void ПриложениеБезОписанияИИдентификаторов_ИщетсяТолькоПоИмени()
    {
        var row = new AppRowViewModel(new AppInfo
        {
            Id = "custom-1",
            DisplayName = "Мой установщик",
            Category = AppCategory.Пользовательские,
            IsUserAdded = true
        });

        Assert.True(row.MatchesSearch("установщик"));
        Assert.False(row.MatchesSearch("архиватор"));
    }
}