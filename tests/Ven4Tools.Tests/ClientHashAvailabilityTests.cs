using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Объяснение, почему у версии клиента нет подтверждённого SHA256. Главное —
/// не называть «CDN недоступен» ситуацию, когда CDN ответил, но ещё не
/// синхронизировался после выхода релиза: это разные советы пользователю.
/// </summary>
public sealed class ClientHashAvailabilityTests
{
    [Fact]
    public void CdnНеОтветил_СообщаетОНедоступности()
    {
        var why = ClientHashAvailability.Explain("5.2.0", cdnManifestLoaded: false, cdnClientVersion: null);

        Assert.Contains("CDN недоступен", why.Reason);
    }

    [Fact]
    public void CdnОтветилНоЕщёНеЗнаетНовуюВерсию_СообщаетОСинхронизации()
    {
        var why = ClientHashAvailability.Explain("5.3.0", cdnManifestLoaded: true, cdnClientVersion: "5.2.0");

        Assert.DoesNotContain("недоступен", why.Reason);
        Assert.Contains("не успел синхронизироваться", why.Reason);
        Assert.Contains("5.2.0", why.Reason);
    }

    [Fact]
    public void ТаЖеВерсияБезХеша_НеВыдаётсяЗаНедоступностьИлиСинхронизацию()
    {
        var why = ClientHashAvailability.Explain("5.2.0", cdnManifestLoaded: true, cdnClientVersion: "5.2.0");

        Assert.DoesNotContain("недоступен", why.Reason);
        Assert.DoesNotContain("синхронизироваться", why.Reason);
        Assert.Contains("SHA256", why.Reason);
    }

    [Fact]
    public void СтараяВерсия_ПредлагаетТекущуюИлиУстановкуИзФайла()
    {
        var why = ClientHashAvailability.Explain("5.0.0", cdnManifestLoaded: true, cdnClientVersion: "5.2.0");

        Assert.DoesNotContain("недоступен", why.Reason);
        Assert.Contains("5.2.0", why.Advice);
        Assert.Contains("Установить из файла", why.Advice);
    }

    [Fact]
    public void МанифестБезСведенийОКлиенте_НеВыдаётсяЗаНедоступность()
    {
        var why = ClientHashAvailability.Explain("5.2.0", cdnManifestLoaded: true, cdnClientVersion: null);

        Assert.DoesNotContain("недоступен", why.Reason);
    }
}
