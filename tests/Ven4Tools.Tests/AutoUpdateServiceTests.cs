using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Автообновление программ: разбор таблицы «winget upgrade» в идентификаторы,
/// исключения и расписание. Сам winget здесь не запускается.
/// </summary>
public class AutoUpdateServiceTests
{
    // Вывод winget на русской Windows: заголовок локализован, перед ним — обрывки
    // индикатора хода, значения выровнены по началу названия колонки.
    private static string Row(string name, string id, string version, string available, string source) =>
        name.PadRight(26) + id.PadRight(22) + version.PadRight(11) + available.PadRight(11) + source;

    private static readonly string RussianOutput =
        "\r   - \r   \\ \r" + new string(' ', 80) + "\r" +
        Row("Имя", "ИД", "Версия", "Доступно", "Источник") + "\r\n" +
        new string('-', 78) + "\r\n" +
        Row("Mozilla Firefox (x64 ru)", "Mozilla.Firefox", "156.0", "157.0", "winget") + "\r\n" +
        Row("7-Zip 26.01 (x64)", "7zip.7zip", "26.01", "26.02", "winget") + "\r\n" +
        // Идентификатор усечён многоточием — winget так делает в узком окне.
        Row("Усечённый идентификатор", "Very.Long.Package.Id…", "1.0", "1.1", "winget") + "\r\n" +
        // Название шире своей колонки: всё, что правее, сдвинуто.
        "Название, которое шире своей колонки " + Row("", "Some.Vendor.App", "1.0", "2.0", "winget").TrimStart() + "\r\n" +
        "Доступны обновления: 4.\r\n";

    [Fact]
    public void ТаблицаОбновлений_РазбираетсяПоКолонкамЗаголовка()
    {
        var entries = AutoUpdateService.ParseUpgrades(RussianOutput);

        Assert.Contains(entries, e => e is { Id: "Mozilla.Firefox", Name: "Mozilla Firefox (x64 ru)", Version: "156.0", Available: "157.0" });
        Assert.Contains(entries, e => e is { Id: "7zip.7zip", Available: "26.02" });
    }

    [Fact]
    public void НеразборчивыйИдентификатор_Пропускается_АНеУгадывается()
    {
        var entries = AutoUpdateService.ParseUpgrades(RussianOutput);

        // Усечённый многоточием идентификатор и строка со сдвинутыми колонками
        // обновлению не подлежат: по такому значению можно обновить не то.
        Assert.Equal(new[] { "Mozilla.Firefox", "7zip.7zip" }, entries.Select(e => e.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Обновлений не найдено.")]
    [InlineData("Имя ИД\r\n")]
    public void БезТаблицы_ПустойСписок(string output) =>
        Assert.Empty(AutoUpdateService.ParseUpgrades(output));

    [Fact]
    public void Исключения_НеПопадаютВОбновление()
    {
        var entries = AutoUpdateService.ParseUpgrades(RussianOutput);

        var (targets, excluded) = AutoUpdateService.SelectTargets(entries, new[] { "mozilla.firefox" });

        Assert.Equal(new[] { "7zip.7zip" }, targets.Select(t => t.Id));
        Assert.Equal(new[] { "Mozilla.Firefox" }, excluded);
    }

    [Fact]
    public void СписокИсключений_РазбираетсяИзТекстаНастроек()
    {
        var ids = AutoUpdateService.ParseExcluded("Mozilla.Firefox\r\n  7zip.7zip, mozilla.firefox ; \"кавычки\" Git.Git");

        Assert.Equal(new[] { "Mozilla.Firefox", "7zip.7zip", "Git.Git" }, ids);
    }

    [Fact]
    public void Расписание_ПервыйЗапуск_Пора() =>
        Assert.True(AutoUpdateService.IsDue(null, AutoUpdateService.Weekly, DateTime.UtcNow));

    [Theory]
    [InlineData("daily", 23, false)]
    [InlineData("daily", 25, true)]
    [InlineData("weekly", 25, false)]
    [InlineData("weekly", 24 * 7 + 1, true)]
    [InlineData("что-то другое", 25, false)]      // незнакомое значение — как «раз в неделю»
    public void Расписание_ПоПрошедшемуВремени(string frequency, int hoursAgo, bool expected)
    {
        var now = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(expected, AutoUpdateService.IsDue(now.AddHours(-hoursAgo), frequency, now));
    }

    [Fact]
    public void Расписание_ПрошлыйЗапускВБудущем_НеОткладываетНавсегда()
    {
        var now = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(AutoUpdateService.IsDue(now.AddDays(30), AutoUpdateService.Weekly, now));
    }

    [Fact]
    public void ЗаданиеОбновитьПрограммы_РазбираетсяИзКоманднойСтроки()
    {
        var status = UnattendedCommandLine.Parse(
            new[] { "--update-apps", "--silent" }, _ => throw new InvalidOperationException(), out var request, out _);

        Assert.Equal(UnattendedCommandLine.ParseStatus.Ok, status);
        Assert.True(request!.UpdateApps);
        Assert.True(request.Silent);
        Assert.Empty(request.AppIds);
    }

    [Fact]
    public void ОбновлениеИУстановкаНабора_ОднимЗапускомНеСовмещаются()
    {
        var status = UnattendedCommandLine.Parse(
            new[] { "--update-apps", "--install", "V4T:vlc" }, _ => "", out _, out string error);

        Assert.Equal(UnattendedCommandLine.ParseStatus.Error, status);
        Assert.NotEmpty(error);
    }
}
