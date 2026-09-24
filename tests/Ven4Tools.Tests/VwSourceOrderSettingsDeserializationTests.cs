using Newtonsoft.Json;
using Ven4Tools.Models;

namespace Ven4Tools.Tests;

/// <summary>
/// Сохранённый порядок источников должен заменять порядок по умолчанию, а не
/// дописываться за ним (поведение Newtonsoft по умолчанию для списка, уже
/// заполненного инициализатором свойства).
/// </summary>
public sealed class VwSourceOrderSettingsDeserializationTests
{
    [Fact]
    public void СохранённыйПорядок_ЗаменяетПорядокПоУмолчанию()
    {
        const string json = "{\"Mode\":\"global\",\"GlobalOrder\":[\"choco\",\"direct\",\"winget\"]}";

        var loaded = JsonConvert.DeserializeObject<SourceOrderSettings>(json);

        Assert.NotNull(loaded);
        Assert.Equal(
            new[] { SourceOrderSettings.Choco, SourceOrderSettings.Direct, SourceOrderSettings.Winget },
            loaded!.GlobalOrder);
    }

    [Fact]
    public void ПовторноеСохранениеИЧтение_НеПлодитДубли()
    {
        var settings = new SourceOrderSettings();

        var roundTrip = JsonConvert.DeserializeObject<SourceOrderSettings>(
            JsonConvert.SerializeObject(settings));

        Assert.Equal(settings.GlobalOrder, roundTrip!.GlobalOrder);
    }
}
