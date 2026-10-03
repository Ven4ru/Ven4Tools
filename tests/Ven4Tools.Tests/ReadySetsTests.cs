using System.Text.Json;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Готовые наборы «Обзора» ссылаются на программы каталога по идентификаторам.
/// Программу могут убрать из каталога или переименовать — тогда набор молча
/// поредел бы; тест ловит это при сборке.
/// </summary>
public class ReadySetsTests
{
    private static HashSet<string> CatalogIds()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Catalog", "master.json")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir != null, "Не найден Catalog/master.json — тест запущен вне дерева репозитория.");

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, "Catalog", "master.json")));
        return doc.RootElement.GetProperty("apps").EnumerateArray()
            .Select(app => app.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ВсеПрограммыНаборов_ЕстьВКаталоге()
    {
        var catalog = CatalogIds();

        foreach (var set in ReadySets.All)
        {
            var missing = set.AppIds.Where(id => !catalog.Contains(id)).ToList();
            Assert.True(missing.Count == 0, $"Набор «{set.Title}»: нет в каталоге — {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void НаборыНеПустыИБезПовторов()
    {
        Assert.Equal(ReadySets.All.Count, ReadySets.All.Select(set => set.Key).Distinct().Count());
        foreach (var set in ReadySets.All)
        {
            Assert.NotEmpty(set.AppIds);
            Assert.Equal(set.AppIds.Count, set.AppIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            // Подсказка кнопки набора — предложение с точкой, как у остальных кнопок.
            Assert.EndsWith(".", set.Description);
        }
    }

    [Fact]
    public void НаборНаходитсяПоКлючу()
    {
        Assert.Equal("Для дома", ReadySets.Find("home")?.Title);
        Assert.Null(ReadySets.Find("нет такого"));
    }
}
