using System.Text;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

public sealed class CatalogSecurityTests
{
    [Fact]
    public void PublishedCatalog_HasValidSignature()
    {
        string json = File.ReadAllText(FixturePath("master.json"), Encoding.UTF8);
        string signature = File.ReadAllText(FixturePath("master.json.sig"), Encoding.UTF8);

        Assert.True(CatalogSignatureVerifier.Verify(json, signature));
    }

    [Fact]
    public void ModifiedCatalog_IsRejected()
    {
        string json = File.ReadAllText(FixturePath("master.json"), Encoding.UTF8);
        string signature = File.ReadAllText(FixturePath("master.json.sig"), Encoding.UTF8);

        Assert.False(CatalogSignatureVerifier.Verify(json + " ", signature));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("AA==")]
    public void MalformedSignature_IsRejected(string signature)
    {
        Assert.False(CatalogSignatureVerifier.Verify("{}", signature));
    }

    // Каталог Linux подписан тем же ключом, что и master.json: подпись его не отличит,
    // Windows-клиент отличает по полю kind.
    [Fact]
    public void LinuxCatalog_IsNotWindowsCatalog()
    {
        var linux = Newtonsoft.Json.JsonConvert.DeserializeObject<Ven4Tools.Models.MasterCatalog>(
            "{\"kind\":\"ven4tools-linux-catalog\",\"version\":99,\"apps\":[]}")!;
        Assert.False(linux.IsWindowsCatalog);
    }

    [Fact]
    public void PublishedMasterCatalog_IsWindowsCatalog()
    {
        string json = File.ReadAllText(FixturePath("master.json"), Encoding.UTF8);
        var master = Newtonsoft.Json.JsonConvert.DeserializeObject<Ven4Tools.Models.MasterCatalog>(json)!;
        Assert.True(master.IsWindowsCatalog);
        Assert.True(master.Apps.Count > 0);
    }

    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
    }
}
