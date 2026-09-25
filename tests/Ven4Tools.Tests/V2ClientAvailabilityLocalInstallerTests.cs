using System.Net.Http;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Приложение из перетащенного локального установщика: Id «User.…», без winget,
/// ссылок и choco — единственный источник лежит на диске. Проверка доступности
/// обязана видеть его, иначе строка каталога становится невыбираемой и файл,
/// добавленный пользователем, поставить нельзя.
/// </summary>
public sealed class V2ClientAvailabilityLocalInstallerTests
{
    private static AvailabilityChecker CheckerWithoutNetwork() =>
        new(new HttpClient(new FailingHandler()));

    [Fact]
    public async Task СуществующийЛокальныйФайл_ДаётAvailable_БезСети()
    {
        using var dir = new TemporaryDirectory();
        string installer = Path.Combine(dir.Path, "setup.exe");
        await File.WriteAllBytesAsync(installer, new byte[] { 1, 2, 3 });

        using var checker = CheckerWithoutNetwork();
        var (status, sizeMb) = await checker.CheckAppAvailabilityWithSize(new AppInfo
        {
            Id = "User.local01",
            DisplayName = "Local App",
            LocalInstallerPath = installer,
            IsUserAdded = true
        });

        Assert.Equal(AvailabilityChecker.AvailabilityStatus.Available, status);
        Assert.True(sizeMb >= 1);
    }

    [Fact]
    public async Task ОтсутствующийЛокальныйФайл_НеСчитаетсяДоступным()
    {
        using var dir = new TemporaryDirectory();

        using var checker = CheckerWithoutNetwork();
        var (status, _) = await checker.CheckAppAvailabilityWithSize(new AppInfo
        {
            Id = "User.local02",
            DisplayName = "Missing Local App",
            LocalInstallerPath = Path.Combine(dir.Path, "missing.exe"),
            IsUserAdded = true
        });

        // Не Available — точный статус зависит от режимов профиля (параноидальный
        // режим отвечает Unknown), а суть проверки в том, что пропавший файл
        // доступным не объявляется.
        Assert.NotEqual(AvailabilityChecker.AvailabilityStatus.Available, status);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Сеть в этом тесте не нужна");
    }
}
