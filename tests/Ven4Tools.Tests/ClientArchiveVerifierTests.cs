using System.IO.Compression;
using System.Text.Json;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class ClientArchiveVerifierTests
{
    private static (string CanonicalHash, ClientArchiveSignatureFile Signature) ReadFixture(string fileName)
    {
        using var archive = ZipFile.OpenRead(FixturePath(fileName));
        var entry = archive.GetEntry(CanonicalArchiveHasher.SignatureEntryName)
            ?? throw new InvalidOperationException("Фикстура без подписи — используйте signed-фикстуру.");
        using var reader = new StreamReader(entry.Open());
        var signature = JsonSerializer.Deserialize<ClientArchiveSignatureFile>(reader.ReadToEnd())!;
        string canonicalHash = CanonicalArchiveHasher.ComputeHex(archive);
        return (canonicalHash, signature);
    }

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    [Fact]
    public void FixtureSignature_IsValid()
    {
        var (hash, sig) = ReadFixture("client-archive-signed-sample.zip");
        Assert.True(ClientArchiveVerifier.Verify(sig.Version!, hash, sig.Signature));
    }

    [Fact]
    public void WrongVersion_IsRejected()
    {
        var (hash, sig) = ReadFixture("client-archive-signed-sample.zip");
        Assert.False(ClientArchiveVerifier.Verify("9.9.9-different", hash, sig.Signature));
    }

    [Fact]
    public void TamperedHash_IsRejected()
    {
        var (hash, sig) = ReadFixture("client-archive-signed-sample.zip");
        Assert.False(ClientArchiveVerifier.Verify(sig.Version!, hash + "00", sig.Signature));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64")]
    public void MalformedSignature_IsRejected(string? signature)
    {
        var (hash, sig) = ReadFixture("client-archive-signed-sample.zip");
        Assert.False(ClientArchiveVerifier.Verify(sig.Version!, hash, signature));
    }
}
