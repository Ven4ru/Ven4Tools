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
    public void Available_НеПоказываетСноскуДажеЕслиОнаЕсть()
    {
        // Главный принцип спеки: вердикт даёт замер, успех гасит сноску.
        var row = RowWithNote("разработчик ушёл из РФ, загрузка через VPN");
        row.Availability = AppRowViewModel.RowAvailability.Available;

        Assert.DoesNotContain("🌍", row.StatusTooltip);
        Assert.DoesNotContain("разработчик ушёл", row.StatusTooltip);
    }
}
