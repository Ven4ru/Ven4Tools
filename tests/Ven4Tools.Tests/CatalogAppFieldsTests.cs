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

    [Fact]
    public void App_ReadsEnglishDescription_AndShowsRussianWhileInterfaceIsRussian()
    {
        const string json = """
        {
          "id": "firefox",
          "name": "Mozilla Firefox",
          "description": "Быстрый, безопасный браузер.",
          "descriptionEn": "A fast, secure browser."
        }
        """;

        var app = JsonConvert.DeserializeObject<Ven4Tools.Models.App>(json)!;

        Assert.Equal("A fast, secure browser.", app.DescriptionEn);
        // Перевод в проверках не включён: показывается исходное описание.
        Assert.Equal("Быстрый, безопасный браузер.", app.LocalizedDescription);
        // Вычисляемое свойство в файл не пишется: иначе оно попало бы в сохранённые списки.
        Assert.DoesNotContain("LocalizedDescription", JsonConvert.SerializeObject(app));
    }

    [Fact]
    public void App_WithoutEnglishDescription_FallsBackToRussian()
    {
        var app = JsonConvert.DeserializeObject<Ven4Tools.Models.App>("""{"id":"x","name":"X","description":"Описание"}""")!;

        Assert.Equal("", app.DescriptionEn);
        Assert.Equal("Описание", app.LocalizedDescription);
    }

    [Fact]
    public void ChangelogEntry_ReadsEnglishNote()
    {
        var entry = JsonConvert.DeserializeObject<Ven4Tools.Models.CatalogChangelogEntry>(
            """{"version":25,"date":"2026-10-07","addedApps":[],"message":"Заметка","messageEn":"Note"}""")!;

        Assert.Equal("Заметка", entry.Message);
        Assert.Equal("Note", entry.MessageEn);
    }

    [Fact]
    public void PublishedCatalog_HasEnglishDescriptionForEveryApp()
    {
        // Каталог обновляется чаще программы, поэтому английское описание новой записи
        // должно приходить вместе с ней: в языковом пакете уже выпущенного клиента его нет.
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "master.json");
        var catalog = JsonConvert.DeserializeObject<Ven4Tools.Models.MasterCatalog>(File.ReadAllText(path))!;

        var withoutEnglish = catalog.Apps
            .Where(a => !string.IsNullOrWhiteSpace(a.Description) && string.IsNullOrWhiteSpace(a.DescriptionEn))
            .Select(a => a.Id)
            .ToList();

        Assert.True(withoutEnglish.Count == 0, "Нет descriptionEn у записей каталога: " + string.Join(", ", withoutEnglish));
    }
}
