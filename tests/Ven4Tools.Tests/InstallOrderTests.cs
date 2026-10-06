using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Порядок установки набора: как выбрали, а драйверные пакеты — в конце.
/// </summary>
public sealed class InstallOrderTests
{
    private sealed record Item(string Id, string? Category);

    private static string[] Arrange(Item[] items, string[]? preferred = null) =>
        InstallOrder.Arrange(items, i => i.Id, i => i.Category, preferred).Select(i => i.Id).ToArray();

    [Fact]
    public void Без_пожеланий_порядок_исходный()
    {
        var items = new[] { new Item("firefox", "Браузеры"), new Item("7zip", "Системные"), new Item("vlc", "Мультимедиа") };

        Assert.Equal(new[] { "firefox", "7zip", "vlc" }, Arrange(items));
    }

    [Fact]
    public void Порядок_выбора_сильнее_порядка_каталога()
    {
        var items = new[] { new Item("7zip", "Системные"), new Item("firefox", "Браузеры"), new Item("vlc", "Мультимедиа") };

        Assert.Equal(new[] { "vlc", "7zip", "firefox" }, Arrange(items, new[] { "vlc", "7ZIP", "firefox" }));
    }

    [Fact]
    public void Не_названные_в_пожеланиях_идут_после_названных_сохраняя_взаимный_порядок()
    {
        var items = new[] { new Item("a", null), new Item("b", null), new Item("c", null), new Item("d", null) };

        Assert.Equal(new[] { "c", "a", "b", "d" }, Arrange(items, new[] { "c" }));
    }

    [Fact]
    public void Драйверные_пакеты_уходят_в_конец_даже_если_выбраны_первыми()
    {
        var items = new[]
        {
            new Item("nvidia-app", "Драйверпаки"), new Item("firefox", "Браузеры"),
            new Item("ddu", " драйверпаки "), new Item("7zip", "Системные")
        };

        Assert.Equal(new[] { "firefox", "7zip", "nvidia-app", "ddu" },
            Arrange(items, new[] { "nvidia-app", "firefox", "ddu", "7zip" }));
    }

    [Fact]
    public void Повторы_в_пожеланиях_и_пустой_набор_не_ломают_порядок()
    {
        Assert.Empty(Arrange(Array.Empty<Item>(), new[] { "a" }));

        var items = new[] { new Item("a", null), new Item("b", null) };
        Assert.Equal(new[] { "b", "a" }, Arrange(items, new[] { "b", "a", "b" }));
    }

    [Theory]
    [InlineData("Драйверпаки", true)]
    [InlineData("драйверпаки", true)]
    [InlineData("Системные", false)]
    [InlineData(null, false)]
    public void Категория_с_драйверами_распознаётся(string? category, bool expected)
    {
        Assert.Equal(expected, InstallOrder.IsRebootProne(category));
    }
}
