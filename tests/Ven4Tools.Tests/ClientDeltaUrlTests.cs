using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Построение URL отдельного файла публикации и проверка относительных путей из
/// файлового манифеста. Обе функции чистые, но ошибка в любой из них проявилась бы
/// только в бою: неверный URL — как «файл недоступен», пропущенный обход пути —
/// как запись мимо папки клиента.
/// </summary>
public sealed class ClientDeltaUrlTests
{
    [Theory]
    [InlineData("https://cdn.ven4tools.ru/client-files/5.1.0/", "Ven4Tools.dll",
                "https://cdn.ven4tools.ru/client-files/5.1.0/Ven4Tools.dll")]
    // Базовый URL без завершающего слэша не должен склеивать имя файла с версией.
    [InlineData("https://cdn.ven4tools.ru/client-files/5.1.0", "Ven4Tools.dll",
                "https://cdn.ven4tools.ru/client-files/5.1.0/Ven4Tools.dll")]
    // Разделители подкаталогов остаются разделителями, а не превращаются в %2F.
    [InlineData("https://cdn.ven4tools.ru/client-files/5.1.0/", "Resources/Fonts/Inter.ttf",
                "https://cdn.ven4tools.ru/client-files/5.1.0/Resources/Fonts/Inter.ttf")]
    // Пробел в имени файла обязан быть экранирован — иначе URL просто невалиден.
    [InlineData("https://cdn.ven4tools.ru/client-files/5.1.0/", "Resources/иконка большая.png",
                "https://cdn.ven4tools.ru/client-files/5.1.0/Resources/%D0%B8%D0%BA%D0%BE%D0%BD%D0%BA%D0%B0%20%D0%B1%D0%BE%D0%BB%D1%8C%D1%88%D0%B0%D1%8F.png")]
    public void CombineUrl_BuildsFileUrlFromBaseAndRelativePath(string baseUrl, string relative, string expected)
    {
        Assert.Equal(expected, ClientDeltaInstaller.CombineUrl(baseUrl, relative));
    }

    [Fact]
    public void CombineUrl_ProducesUrlAcceptedByDownloadValidator()
    {
        // Собранная ссылка обязана проходить штатный allowlist доверенных хостов —
        // иначе загрузка файла отвалится на первом же кандидате.
        Assert.True(DownloadValidator.IsAllowedDownloadHost(
            ClientDeltaInstaller.CombineUrl("https://cdn.ven4tools.ru/client-files/5.1.0/", "Ven4Tools.dll")));
        Assert.True(DownloadValidator.IsAllowedDownloadHost(
            ClientDeltaInstaller.CombineUrl("https://ven4tools.ru/releases/client-files/5.1.0/", "Ven4Tools.dll")));

        // Зеркало на хостинге доверено только внутри /releases/ — за его пределами нет.
        Assert.False(DownloadValidator.IsAllowedDownloadHost(
            ClientDeltaInstaller.CombineUrl("https://ven4tools.ru/api/", "Ven4Tools.dll")));
    }

    [Theory]
    [InlineData("Ven4Tools.dll", true)]
    [InlineData("Resources/Fonts/Inter.ttf", true)]
    [InlineData("папка/файл.dat", true)]
    [InlineData("../evil.dll", false)]
    [InlineData("sub/../../evil.dll", false)]
    [InlineData("Resources\\Fonts\\Inter.ttf", false)]
    [InlineData("/Ven4Tools.dll", false)]
    [InlineData("C:/Windows/evil.dll", false)]
    [InlineData("Resources//Inter.ttf", false)]
    [InlineData("Resources/", false)]
    [InlineData("./Ven4Tools.dll", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSafeRelativePath_AcceptsOnlyManifestStylePaths(string? path, bool expected)
    {
        Assert.Equal(expected, ManifestPathGuard.IsSafeRelativePath(path));
    }

    [Fact]
    public void ResolveInside_ReturnsPathUnderClientFolder()
    {
        string resolved = ManifestPathGuard.ResolveInside(@"C:\Ven4Tools\Ven4Tools_Client", "Resources/Fonts/Inter.ttf");

        Assert.Equal(@"C:\Ven4Tools\Ven4Tools_Client\Resources\Fonts\Inter.ttf", resolved);
    }

    [Fact]
    public void ResolveInside_RejectsEscapeFromClientFolder()
    {
        Assert.Throws<InvalidOperationException>(
            () => ManifestPathGuard.ResolveInside(@"C:\Ven4Tools\Ven4Tools_Client", "../evil.dll"));
    }
}
