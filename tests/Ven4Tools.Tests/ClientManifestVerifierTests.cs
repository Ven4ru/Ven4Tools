using System.Text;
using System.Text.Json;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Подпись файлового манифеста клиента. Фикстура выпущена реальным
/// Tools/ClientManifestSigner — значит тест сторожит и то, что вшитый в лаунчер
/// публичный ключ соответствует ключу подписи, и то, что формат манифеста,
/// который пишет инструмент, разбирается моделью лаунчера.
/// </summary>
public sealed class ClientManifestVerifierTests
{
    [Fact]
    public void SignedFixture_HasValidSignature()
    {
        Assert.True(ClientManifestVerifier.Verify(FixtureJson(), FixtureSignature()));
    }

    [Fact]
    public void ModifiedManifest_IsRejected()
    {
        Assert.False(ClientManifestVerifier.Verify(FixtureJson() + " ", FixtureSignature()));
    }

    [Fact]
    public void UpdateManifestSignature_DoesNotVerifyAsClientManifest()
    {
        // Domain separation между типами манифеста: version.json описывает архив
        // релиза целиком, client-manifest.json — какие ОТДЕЛЬНЫЕ файлы лягут внутрь
        // папки клиента. Подпись одного не должна приниматься за подпись другого.
        string json = File.ReadAllText(FixturePath("version-manifest-sample.json"), Encoding.UTF8);
        string signature = File.ReadAllText(FixturePath("version-manifest-sample.json.sig"), Encoding.UTF8);

        Assert.False(ClientManifestVerifier.Verify(json, signature));
    }

    [Fact]
    public void ClientManifestSignature_DoesNotVerifyAsUpdateManifest()
    {
        // Обратная проверка того же свойства.
        Assert.False(UpdateManifestVerifier.Verify(FixtureJson(), FixtureSignature()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("AA==")]
    public void MalformedOrMissingSignature_IsRejected(string? signature)
    {
        Assert.False(ClientManifestVerifier.Verify("{}", signature));
    }

    [Fact]
    public void SignedFixture_DeserializesIntoLauncherModel()
    {
        var manifest = JsonSerializer.Deserialize<ClientFileManifest>(FixtureJson());

        Assert.NotNull(manifest);
        Assert.Equal("5.1.0", manifest!.Version);
        Assert.Equal(3, manifest.Files!.Count);
        // Пути в манифесте — относительные, через '/', отсортированные по алфавиту.
        Assert.Equal("Resources/Fonts/Inter.ttf", manifest.Files[0].Path);
        Assert.All(manifest.Files, f => Assert.True(ManifestPathGuard.IsSafeRelativePath(f.Path)));
        Assert.All(manifest.Files, f => Assert.True(DownloadValidator.IsValidSha256(f.Sha256)));
    }

    private static string FixtureJson() =>
        File.ReadAllText(FixturePath("client-manifest-sample.json"), Encoding.UTF8);

    private static string FixtureSignature() =>
        File.ReadAllText(FixturePath("client-manifest-sample.json.sig"), Encoding.UTF8);

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
}
