using Ven4Tools.Models;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

public sealed class AppRowViewModelRegionTests
{
    private static AppRowViewModel RowWithNote(string? regionNote) =>
        new(new AppInfo
        {
            Id = "example-app",
            DisplayName = "Example",
            RegionNote = regionNote
        });

    [Fact]
    public void RegionBlocked_НеSelectable_КакUnavailable()
    {
        var row = RowWithNote(null);
        row.Availability = AppRowViewModel.RowAvailability.RegionBlocked;

        Assert.False(row.IsSelectable);
    }

    [Fact]
    public void RegionBlocked_ПоказываетКнопкуПредложитьИсточник()
    {
        var row = RowWithNote(null);
        row.Availability = AppRowViewModel.RowAvailability.RegionBlocked;

        Assert.True(row.ShowSuggestButton);
    }

    [Fact]
    public void RegionBlocked_БезСноски_ТултипОбщаяФормулировка()
    {
        var row = RowWithNote(null);
        row.Availability = AppRowViewModel.RowAvailability.RegionBlocked;

        Assert.StartsWith("🌍", row.StatusTooltip);
        Assert.DoesNotContain("null", row.StatusTooltip);
    }

    [Fact]
    public void RegionBlocked_СоСноской_ТултипПоказываетТекстСноски()
    {
        var row = RowWithNote("разработчик ушёл из РФ, загрузка через VPN");
        row.Availability = AppRowViewModel.RowAvailability.RegionBlocked;

        Assert.Equal("🌍 разработчик ушёл из РФ, загрузка через VPN", row.StatusTooltip);
    }

    [Fact]
    public void RegionBlocked_ПоказываетЗначокГлобуса()
    {
        var row = RowWithNote(null);
        row.Availability = AppRowViewModel.RowAvailability.RegionBlocked;

        Assert.True(row.ShowRegionBlockedGlyph);
    }

    [Theory]
    [InlineData(AppRowViewModel.RowAvailability.Available)]
    [InlineData(AppRowViewModel.RowAvailability.Unavailable)]
    [InlineData(AppRowViewModel.RowAvailability.Unknown)]
    [InlineData(AppRowViewModel.RowAvailability.Checking)]
    public void ЗначокГлобуса_ПоказываетсяТолькоПриRegionBlocked(AppRowViewModel.RowAvailability availability)
    {
        var row = RowWithNote("разработчик ушёл из РФ");
        row.Availability = availability;

        Assert.False(row.ShowRegionBlockedGlyph);
    }

    [Fact]
    public void ТултипЗначка_БезДублирующегоГлобуса_ЭмодзиУжеВСамомЗначке()
    {
        var withNote = RowWithNote("разработчик ушёл из РФ, загрузка через VPN");
        withNote.Availability = AppRowViewModel.RowAvailability.RegionBlocked;

        var withoutNote = RowWithNote(null);
        withoutNote.Availability = AppRowViewModel.RowAvailability.RegionBlocked;

        Assert.Equal("разработчик ушёл из РФ, загрузка через VPN", withNote.RegionBlockedTooltip);
        Assert.DoesNotContain("🌍", withNote.RegionBlockedTooltip);
        Assert.DoesNotContain("🌍", withoutNote.RegionBlockedTooltip);
        Assert.DoesNotContain("null", withoutNote.RegionBlockedTooltip);
        Assert.NotEmpty(withoutNote.RegionBlockedTooltip);
    }

    [Fact]
    public void RegionBlocked_НеПерекрашиваетНазваниеПриложения()
    {
        // Правка пользователя и буква спеки: маркер региона — отдельный значок 🌍
        // рядом с названием, а НЕ ещё один цвет названия. Название остаётся того же
        // цвета, что у Unknown/Checking, и заведомо не совпадает с «недоступно».
        var regionBlocked = RowWithNote(null);
        regionBlocked.Availability = AppRowViewModel.RowAvailability.RegionBlocked;

        var unknown = RowWithNote(null);
        unknown.Availability = AppRowViewModel.RowAvailability.Unknown;

        var unavailable = RowWithNote(null);
        unavailable.Availability = AppRowViewModel.RowAvailability.Unavailable;

        Assert.Same(unknown.RowBrush, regionBlocked.RowBrush);
        Assert.NotSame(unavailable.RowBrush, regionBlocked.RowBrush);
    }

    [Fact]
    public void Available_НеПоказываетСноскуДажеЕслиОнаЕсть()
    {
        // Главный принцип спеки: вердикт даёт замер, успех гасит сноску.
        var row = RowWithNote("разработчик ушёл из РФ, загрузка через VPN");
        row.Availability = AppRowViewModel.RowAvailability.Available;

        Assert.DoesNotContain("🌍", row.StatusTooltip);
        Assert.DoesNotContain("разработчик ушёл", row.StatusTooltip);
    }
}
