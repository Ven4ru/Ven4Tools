using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>Разбор вывода powercfg о режиме усиления процессора и подписи режимов.</summary>
public class TurboBoostModesTests
{
    // Вывод powercfg /qh на русской Windows 11 (подписи строк переведены системой).
    private const string RussianOutput = """
        GUID схемы питания: 381b4222-f694-41f0-9685-ff5bb260df2e  (Сбалансированная)
          GUID подгруппы: 54533251-82be-4824-96c1-47b60b740d00  (Управление питанием процессора)
            GUID параметра питания: be337238-0d82-4146-a960-4f3749d470c7  (Режим усиления производительности процессора)
              Индекс возможной настройки: 000
              Понятное имя возможной настройки: Отключен
              Индекс возможной настройки: 001
              Понятное имя возможной настройки: Включен
              Индекс возможной настройки: 002
              Понятное имя возможной настройки: Агрессивный
              Индекс возможной настройки: 003
              Понятное имя возможной настройки: Эффективно включенный
              Индекс возможной настройки: 004
              Понятное имя возможной настройки: Эффективно агрессивный
              Индекс возможной настройки: 005
              Понятное имя возможной настройки: Агрессивный при гарантированном
              Индекс возможной настройки: 006
              Понятное имя возможной настройки: Эффективно агрессивный при гарантированном
            Текущий индекс настройки питания от сети: 0x00000001
            Текущий индекс настройки питания от батарей: 0x00000002
        """;

    private const string EnglishOutput = """
        Power Scheme GUID: 8C5E7FDA-E8BF-4A96-9A85-A6E23A8C635C  (High performance)
          GUID Alias: SCHEME_MIN
          Subgroup GUID: 54533251-82be-4824-96c1-47b60b740d00  (Processor power management)
            GUID Alias: SUB_PROCESSOR
            Power Setting GUID: be337238-0d82-4146-a960-4f3749d470c7  (Processor performance boost mode)
              GUID Alias: PERFBOOSTMODE
              Possible Setting Index: 000
              Possible Setting Friendly Name: Disabled
              Possible Setting Index: 001
              Possible Setting Friendly Name: Enabled
              Possible Setting Index: 002
              Possible Setting Friendly Name: Aggressive
              Possible Setting Index: 003
              Possible Setting Friendly Name: Efficient Enabled
              Possible Setting Index: 004
              Possible Setting Friendly Name: Efficient Aggressive
            Current AC Power Setting Index: 0x00000002
            Current DC Power Setting Index: 0x00000002
        """;

    [Fact]
    public void Parse_РусскаяWindows_СхемаРежимыИТекущие()
    {
        var state = TurboBoostModes.Parse(RussianOutput);

        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", state.SchemeGuid);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, state.Possible);
        Assert.Equal(1, state.Ac);
        Assert.Equal(2, state.Dc);
    }

    [Fact]
    public void Parse_АнглийскаяWindows_ПятьРежимов_СхемаВНижнемРегистре()
    {
        var state = TurboBoostModes.Parse(EnglishOutput);

        // Регистр важен: по этому идентификатору ищется ветка реестра с заводским режимом.
        Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", state.SchemeGuid);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, state.Possible);
        Assert.Equal(2, state.Ac);
        Assert.Equal(2, state.Dc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Недопустимые параметры — для справки введите \"/?\".")]
    public void Parse_ПустойИлиЧужойВывод_РежимНеизвестенСписокИзДокументации(string? output)
    {
        var state = TurboBoostModes.Parse(output);

        Assert.Null(state.SchemeGuid);
        Assert.Null(state.Ac);
        Assert.Null(state.Dc);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, state.Possible);
    }

    [Fact]
    public void Name_УКаждогоРежимаСвоёНазвание_НезнакомыйНазванНомером()
    {
        var names = Enumerable.Range(0, 7).Select(TurboBoostModes.Name).ToList();

        Assert.Equal(7, names.Distinct().Count());
        Assert.Equal("Отключён", names[0]);
        Assert.Equal("Агрессивный", names[2]);
        Assert.Equal("Режим 9", TurboBoostModes.Name(9));
    }

    [Fact]
    public void Description_ЕстьУВсехИзвестныхРежимов()
    {
        Assert.All(Enumerable.Range(0, 7), mode => Assert.False(string.IsNullOrWhiteSpace(TurboBoostModes.Description(mode))));
        Assert.Equal("", TurboBoostModes.Description(9));
    }

    [Theory]
    [InlineData(false, false, "Включён")]
    [InlineData(true, false, "Включён — текущий")]
    [InlineData(false, true, "Включён — по умолчанию в Windows")]
    [InlineData(true, true, "Включён — текущий, по умолчанию в Windows")]
    public void Label_ПометкиТекущегоИЗаводского(bool isCurrent, bool isDefault, string expected)
    {
        Assert.Equal(expected, TurboBoostModes.Label("Включён", isCurrent, isDefault, "текущий", "по умолчанию в Windows"));
    }
}
