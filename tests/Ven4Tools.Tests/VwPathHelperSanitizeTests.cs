using Ven4Tools.Helpers;

namespace Ven4Tools.Tests;

/// <summary>
/// «.» и «..» проходят фильтр недопустимых символов, но как компонент пути ведут
/// на текущий/родительский каталог — санитайзер обязан их обезвредить.
/// </summary>
public sealed class VwPathHelperSanitizeTests
{
    [Theory]
    [InlineData(".", "_")]
    [InlineData("..", "__")]
    [InlineData(" .. ", "____")]
    public void ТолькоТочки_ЗаменяютсяНаСимволЗамены(string input, string expected)
    {
        Assert.Equal(expected, PathHelper.SanitizeFileNameComponent(input));
    }

    [Theory]
    [InlineData("App.v2", "App.v2")]
    [InlineData("A/B", "A_B")]
    public void ОбычныеИмена_НеМеняютсяКромеНедопустимыхСимволов(string input, string expected)
    {
        Assert.Equal(expected, PathHelper.SanitizeFileNameComponent(input));
    }
}
