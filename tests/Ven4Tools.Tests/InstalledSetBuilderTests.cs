using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// «Набор из установленного»: код набора и файл ответа по тому, что уже стоит на
/// компьютере. Оба должны читаться теми же разборами, что и набор с сайта и тихий режим.
/// </summary>
public class InstalledSetBuilderTests
{
    private static readonly (string Id, string Name, bool Installed)[] Apps =
    {
        ("firefox", "Mozilla Firefox", true),
        ("telegram", "Telegram Desktop", false),
        ("7zip", "7-Zip", true),
        ("vlc", "VLC Media Player", false)
    };

    [Fact]
    public void ВНабор_ПопадаютТолькоУстановленные()
    {
        var set = InstalledSetBuilder.Build(Apps);

        Assert.Equal(new[] { "firefox", "7zip" }, set.AppIds);
        Assert.Equal(new[] { "Mozilla Firefox", "7-Zip" }, set.Names);
        Assert.Equal("V4T:firefox,7zip", set.Code);
    }

    [Fact]
    public void Код_ЧитаетсяРазборомНабораССайта()
    {
        var set = InstalledSetBuilder.Build(Apps);

        var parsed = SitePresetService.Parse(set.Code);

        Assert.True(parsed.Success);
        Assert.Equal(set.AppIds, parsed.AppIds);
    }

    [Fact]
    public void ФайлОтвета_ЧитаетсяТихимРежимом()
    {
        var set = InstalledSetBuilder.Build(Apps);
        string json = InstalledSetBuilder.BuildAnswerFile(set.AppIds);

        var status = UnattendedCommandLine.Parse(
            new[] { "--answer-file", "набор.json" }, _ => json, out var request, out string error);

        Assert.True(status == UnattendedCommandLine.ParseStatus.Ok, error);
        Assert.Equal(set.AppIds, request!.AppIds);
        Assert.True(request.Silent);
        Assert.True(request.RestorePoint);
    }

    [Fact]
    public void НичегоНеУстановлено_ПустойНаборБезКода()
    {
        var set = InstalledSetBuilder.Build(new[] { ("vlc", "VLC", false) });

        Assert.Empty(set.AppIds);
        Assert.Equal("", set.Code);
    }

    [Fact]
    public void НазванияСоответствуютКоду_АНеОбещаютЛишнего()
    {
        // Идентификатор, который разбор кода отбросит, не должен значиться в списке
        // названий: человек увидел бы программу, которой в коде нет.
        var set = InstalledSetBuilder.Build(new[]
        {
            ("firefox", "Mozilla Firefox", true),
            ("моя программа", "Моя программа", true)
        });

        Assert.Equal(new[] { "Mozilla Firefox" }, set.Names);
        Assert.Equal("V4T:firefox", set.Code);
    }
}
