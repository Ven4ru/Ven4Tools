using Newtonsoft.Json;
using Ven4Tools.Models;

namespace Ven4Tools.Tests;

/// <summary>
/// regionNote — необязательное поле каталога: объяснение, а не вердикт,
/// поэтому отсутствие поля не должно быть ошибкой разбора.
/// </summary>
public sealed class CatalogAppRegionNoteTests
{
    [Fact]
    public void RegionNote_ЧитаетсяИзJson()
    {
        const string json = """
        {
            "id": "example-app",
            "name": "Example",
            "regionNote": "издатель ограничил загрузку для вашего региона"
        }
        """;

        var app = JsonConvert.DeserializeObject<Ven4Tools.Models.App>(json)!;

        Assert.Equal("издатель ограничил загрузку для вашего региона", app.RegionNote);
    }

    [Fact]
    public void RegionNote_ОтсутствуетВJson_ДаётNull()
    {
        const string json = """{ "id": "example-app", "name": "Example" }""";

        var app = JsonConvert.DeserializeObject<Ven4Tools.Models.App>(json)!;

        Assert.Null(app.RegionNote);
    }
}
