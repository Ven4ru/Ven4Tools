using System.Text.Json;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Tests;

public sealed class CdnVersionInfoDeserializationTests
{
    private const string Json = """
    {
      "client": { "version": "4.4.3" },
      "launcher": { "version": "3.2.2" },
      "cdn_ip": "138.16.152.133",
      "revokedClientHashes": ["deadbeef"],
      "historicalClientArchives": [
        { "version": "4.4.2", "sha256": "ffce9133" }
      ]
    }
    """;

    [Fact]
    public void Deserialize_ReadsRevokedAndHistoricalFields()
    {
        var info = JsonSerializer.Deserialize<CdnVersionInfo>(Json);

        Assert.NotNull(info);
        Assert.Equal(["deadbeef"], info!.RevokedClientHashes);
        Assert.NotNull(info.HistoricalClientArchives);
        Assert.Single(info.HistoricalClientArchives!);
        Assert.Equal("4.4.2", info.HistoricalClientArchives![0].Version);
        Assert.Equal("ffce9133", info.HistoricalClientArchives[0].Sha256);
    }

    [Fact]
    public void Deserialize_MissingFields_YieldNull()
    {
        var info = JsonSerializer.Deserialize<CdnVersionInfo>("""{ "client": { "version": "4.4.3" } }""");

        Assert.NotNull(info);
        Assert.Null(info!.RevokedClientHashes);
        Assert.Null(info.RevokedClientCanonicalHashes);
        Assert.Null(info.RevokedClientVersions);
        Assert.Null(info.ManifestSeq);
        Assert.Null(info.HistoricalClientArchives);
    }

    [Fact]
    public void Deserialize_ReadsCanonicalAndVersionRevocation()
    {
        var info = JsonSerializer.Deserialize<CdnVersionInfo>("""
        {
          "revokedClientHashes": ["aa"],
          "revokedClientCanonicalHashes": ["bb"],
          "revokedClientVersions": ["6.1.0"],
          "manifest_seq": 1793952000
        }
        """)!;

        Assert.Equal(["aa"], info.RevokedClientHashes);
        Assert.Equal(["bb"], info.RevokedClientCanonicalHashes);
        Assert.Equal(["6.1.0"], info.RevokedClientVersions);
        Assert.Equal(1793952000, info.ManifestSeq);
    }
}
