using System.Text;
using Newtonsoft.Json.Linq;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Категория, которая есть в каталоге, но не известна <see cref="AppCategoryHelper.Parse"/>,
/// молча уезжает в «Другое» — так уже было с категорией «ИИ» в каталоге v17.
/// Проверяем и опубликованный каталог, и встроенный в exe резерв.
/// </summary>
public sealed class CatalogCategoryCoverageTests
{
    [Fact]
    public void PublishedCatalog_AllCategoriesAreKnown()
    {
        string json = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "master.json"), Encoding.UTF8);
        AssertAllCategoriesKnown(json);
    }

    [Fact]
    public void EmbeddedCatalog_AllCategoriesAreKnown()
    {
        using var stream = typeof(CatalogLoaderService).Assembly
            .GetManifestResourceStream("Ven4Tools.Resources.embedded_catalog.json");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!, Encoding.UTF8);
        AssertAllCategoriesKnown(reader.ReadToEnd());
    }

    private static void AssertAllCategoriesKnown(string json)
    {
        var apps = (JArray)JObject.Parse(json)["apps"]!;
        Assert.NotEmpty(apps);

        var unknown = apps
            .Select(a => (Id: (string?)a["id"], Category: (string?)a["category"] ?? ""))
            .Where(a => a.Category != "Другое" && AppCategoryHelper.Parse(a.Category) == AppCategory.Другое)
            .Select(a => $"{a.Id}: «{a.Category}»")
            .ToList();

        Assert.True(unknown.Count == 0, "Неизвестные категории: " + string.Join(", ", unknown));
    }
}
