using Newtonsoft.Json;
using Ven4Tools.Models;

namespace Ven4Tools.Tests;

public sealed class CatalogAppFieldsTests
{
    [Fact]
    public void App_DeserializesDescriptionVersionSize_FromCatalogJson()
    {
        const string json = """
        {
          "id": "firefox",
          "name": "Mozilla Firefox",
          "category": "Браузеры",
          "wingetId": "Mozilla.Firefox",
          "downloadUrl": "https://download.mozilla.org/?product=firefox-latest",
          "version": "152.0.4",
          "size": "84.7 MB",
          "iconUrl": "https://cdn.simpleicons.org/firefox",
          "description": "Быстрый, безопасный браузер."
        }
        """;

        var app = JsonConvert.DeserializeObject<Ven4Tools.Models.App>(json)!;

        Assert.Equal("152.0.4", app.Version);
        Assert.Equal("84.7 MB", app.Size);
        Assert.Equal("Быстрый, безопасный браузер.", app.Description);
    }
}
