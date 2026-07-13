using System.Text;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class UpdateManifestVerifierTests
{
    [Fact]
    public void SignedFixture_HasValidSignature()
    {
        string json = File.ReadAllText(FixturePath("version-manifest-sample.json"), Encoding.UTF8);
        string signature = File.ReadAllText(FixturePath("version-manifest-sample.json.sig"), Encoding.UTF8);

        Assert.True(UpdateManifestVerifier.Verify(json, signature));
    }

    [Fact]
    public void ModifiedManifest_IsRejected()
    {
        string json = File.ReadAllText(FixturePath("version-manifest-sample.json"), Encoding.UTF8);
        string signature = File.ReadAllText(FixturePath("version-manifest-sample.json.sig"), Encoding.UTF8);

        Assert.False(UpdateManifestVerifier.Verify(json + " ", signature));
    }

    [Fact]
    public void CatalogSignature_DoesNotVerifyAsUpdateManifest()
    {
        // Domain separation: подпись, выпущенная CatalogSigner для master.json
        // (другой ключ и другой префикс), не должна проходить как подпись
        // version.json, даже если бы кто-то попытался скопировать байты .sig.
        string json = File.ReadAllText(FixturePath("master.json"), Encoding.UTF8);
        string catalogSignature = File.ReadAllText(FixturePath("master.json.sig"), Encoding.UTF8);

        Assert.False(UpdateManifestVerifier.Verify(json, catalogSignature));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("AA==")]
    public void MalformedOrMissingSignature_IsRejected(string? signature)
    {
        Assert.False(UpdateManifestVerifier.Verify("{}", signature));
    }

    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
    }
}
