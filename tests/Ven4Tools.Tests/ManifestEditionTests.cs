using System.Text.Json;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Редакция подписанного version.json: обычный адрес принимает только обычный манифест,
/// адрес /legacy/ — только манифест версии для Windows 10 без обновлений. Чужой манифест
/// отвергается до учёта номера выпуска и номер не сдвигает.
/// </summary>
public sealed class ManifestEditionTests : IDisposable
{
    private readonly TemporaryDirectory _dir = new();
    private ManifestSequenceGuard Guard() => new(Path.Combine(_dir.Path, "Launcher", ManifestSequenceGuard.FileName));
    public void Dispose() => _dir.Dispose();

    private static CdnVersionInfo Manifest(string? edition, long seq) =>
        JsonSerializer.Deserialize<CdnVersionInfo>(
            "{\"manifest_seq\":" + seq + (edition == null ? "" : ",\"edition\":\"" + edition + "\"") + "}")!;

    [Theory]
    [InlineData("https://cdn.ven4tools.ru/version.json", ManifestEdition.Regular)]
    [InlineData("https://cdn.ven4tools.ru/legacy/version.json", ManifestEdition.Win10Legacy)]
    [InlineData("https://CDN.ven4tools.ru/Legacy/Version.json", ManifestEdition.Win10Legacy)]
    public void ExpectedEdition_ByAddress(string url, string expected) =>
        Assert.Equal(expected, ManifestEdition.ExpectedFor(url));

    [Fact]
    public void ManifestWithoutField_IsRegular()
    {
        var guard = Guard();
        Assert.NotNull(CdnService.AcceptFresh(Manifest(null, 100), ManifestEdition.Regular, guard, out _));
        Assert.Null(CdnService.AcceptFresh(Manifest(null, 101), ManifestEdition.Win10Legacy, Guard(), out var reason));
        Assert.Contains("другой редакции", reason);
    }

    [Fact]
    public void LegacyManifest_RejectedOnRegularAddress_WithoutMovingSequence()
    {
        var guard = Guard();
        Assert.NotNull(CdnService.AcceptFresh(Manifest(null, 100), ManifestEdition.Regular, guard, out _));
        Assert.Null(CdnService.AcceptFresh(Manifest(ManifestEdition.Win10Legacy, 9999), ManifestEdition.Regular, guard, out var reason));
        Assert.Contains("win10legacy", reason);
        Assert.Equal(100, guard.Highest);
        // Настоящий обычный манифест со следующим номером по-прежнему принимается.
        Assert.NotNull(CdnService.AcceptFresh(Manifest(null, 200), ManifestEdition.Regular, guard, out _));
    }

    [Fact]
    public void LegacyManifest_AcceptedOnLegacyAddress()
    {
        Assert.NotNull(CdnService.AcceptFresh(Manifest("Win10Legacy", 100), ManifestEdition.Win10Legacy, Guard(), out _));
    }

    [Fact]
    public void ExplicitRegular_IsRegular()
    {
        Assert.NotNull(CdnService.AcceptFresh(Manifest("regular", 100), ManifestEdition.Regular, Guard(), out _));
    }
}
