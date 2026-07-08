using Ven4Tools.Models;
using Ven4Tools.Services.WindowsUpdate;

namespace Ven4Tools.Tests;

public sealed class WindowsUpdateModelsTests
{
    [Fact]
    public void UserProfile_DefaultsToNotSet()
    {
        var profile = new UserProfile();
        Assert.Equal("NotSet", profile.WindowsUpdateMode);
    }

    [Fact]
    public void SearchResult_Ok_CarriesItems()
    {
        var items = new[] { new WindowsUpdateItem { UpdateId = "abc", Title = "Test" } };
        var result = WindowsUpdateSearchResult.Ok(items);

        Assert.True(result.Success);
        Assert.Single(result.Items);
        Assert.Equal("", result.ErrorMessage);
    }

    [Fact]
    public void SearchResult_Failed_CarriesMessage()
    {
        var result = WindowsUpdateSearchResult.Failed("служба недоступна");

        Assert.False(result.Success);
        Assert.Empty(result.Items);
        Assert.Equal("служба недоступна", result.ErrorMessage);
    }
}
