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

    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
    }
}
