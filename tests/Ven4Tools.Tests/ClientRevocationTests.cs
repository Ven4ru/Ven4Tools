using System.IO.Compression;
using System.Text.Json;
using Ven4Tools.Launcher.Helpers;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Отзыв архивов клиента: по SHA256 файла, по каноническому хешу содержимого и по
/// версии. Главный случай — находка ревью 05.11: тот же подписанный архив, пересжатый
/// заново, имеет другой SHA256 файла и раньше проходил мимо отзыва.
/// </summary>
public sealed class ClientRevocationTests
{
    private const string FileHash = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Canonical = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string FixtureVersion = "9.9.9-fixture";

    [Fact]
    public void IsRevoked_ByFileHash_CaseInsensitive()
    {
        var info = new CdnVersionInfo { RevokedClientHashes = [FileHash.ToUpperInvariant()] };
        Assert.True(ClientRevocation.IsRevoked(info, FileHash, Canonical, "6.0.0"));
    }

    [Fact]
    public void IsRevoked_ByCanonicalHash()
    {
        var info = new CdnVersionInfo { RevokedClientCanonicalHashes = [Canonical] };
        Assert.True(ClientRevocation.IsRevoked(info, FileHash, Canonical, "6.0.0"));
    }

    [Theory]
    [InlineData("6.1.0", "6.1.0")]
    [InlineData("6.1.0", "6.1.0.0")]
    [InlineData("6.1.0.0", "6.1.0")]
    public void IsRevoked_ByVersion_ComparesAsVersions(string revoked, string archiveVersion)
    {
        var info = new CdnVersionInfo { RevokedClientVersions = [revoked] };
        Assert.True(ClientRevocation.IsRevoked(info, FileHash, Canonical, archiveVersion));
        Assert.True(ClientRevocation.IsVersionRevoked(info, archiveVersion));
    }

    [Fact]
    public void IsRevoked_OtherVersionAndHashes_NotRevoked()
    {
        var info = new CdnVersionInfo
        {
            RevokedClientHashes = ["3333333333333333333333333333333333333333333333333333333333333333"],
            RevokedClientCanonicalHashes = ["4444444444444444444444444444444444444444444444444444444444444444"],
            RevokedClientVersions = ["6.1.0", "6.1.0-beta.1"],
        };
        Assert.False(ClientRevocation.IsRevoked(info, FileHash, Canonical, "6.1.1"));
        Assert.False(ClientRevocation.IsRevoked(info, FileHash, Canonical, "6.1.0-beta.2"));
    }

    [Fact]
    public void IsRevoked_NoManifestOrNoLists_NotRevoked()
    {
        Assert.False(ClientRevocation.IsRevoked(null, FileHash, Canonical, "6.0.0"));
        Assert.False(ClientRevocation.IsRevoked(new CdnVersionInfo(), FileHash, Canonical, "6.0.0"));
        Assert.False(ClientRevocation.IsVersionRevoked(new CdnVersionInfo { RevokedClientVersions = ["6.0.0"] }, null));
    }

    [Fact]
    public void OldManifest_WithoutNewFields_StillRevokesByFileHash()
    {
        var info = JsonSerializer.Deserialize<CdnVersionInfo>($$"""{ "revokedClientHashes": ["{{FileHash}}"] }""")!;
        Assert.Null(info.RevokedClientCanonicalHashes);
        Assert.Null(info.RevokedClientVersions);
        Assert.True(ClientRevocation.IsRevoked(info, FileHash, Canonical, "6.0.0"));
    }

    // --- LocalArchiveVerifier на настоящем подписанном архиве ---

    private static string CopyFixture()
    {
        string source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "client-archive-signed-sample.zip");
        string dest = Path.Combine(Path.GetTempPath(), $"revtest_{Guid.NewGuid():N}.zip");
        File.Copy(source, dest);
        return dest;
    }

    // Тот же архив, пересобранный без сжатия: содержимое (и подпись внутри) то же,
    // байты файла — другие.
    private static string Repack(string archivePath)
    {
        string dest = Path.Combine(Path.GetTempPath(), $"revtest_repacked_{Guid.NewGuid():N}.zip");
        using (var source = ZipFile.OpenRead(archivePath))
        using (var target = ZipFile.Open(dest, ZipArchiveMode.Create))
        {
            foreach (var entry in source.Entries)
            {
                var copy = target.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
                if (string.IsNullOrEmpty(entry.Name)) continue;
                using var input = entry.Open();
                using var output = copy.Open();
                input.CopyTo(output);
            }
        }
        return dest;
    }

    private static string CanonicalHashOf(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return CanonicalArchiveHasher.ComputeHex(archive);
    }

    private static Task<LocalArchiveVerificationResult> Verify(string archive, CdnVersionInfo info) =>
        LocalArchiveVerifier.VerifyAsync(archive, _ => Task.FromResult<CdnVersionInfo?>(info), CancellationToken.None);

    [Fact]
    public async Task RepackedSignedArchive_RevokedByCanonicalHash_IsRejected()
    {
        string original = CopyFixture();
        string repacked = Repack(original);
        try
        {
            string originalFileHash = await FileHashHelper.ComputeSha256Async(original, CancellationToken.None);
            string repackedFileHash = await FileHashHelper.ComputeSha256Async(repacked, CancellationToken.None);
            Assert.NotEqual(originalFileHash, repackedFileHash);
            Assert.Equal(CanonicalHashOf(original), CanonicalHashOf(repacked));

            // Раньше: отзыв только по хешу файла — пересжатый архив проходил.
            var byFileOnly = new CdnVersionInfo { RevokedClientHashes = [originalFileHash] };
            var bypass = await Verify(repacked, byFileOnly);
            Assert.Equal(LocalArchiveOutcome.Offline, bypass.Outcome);

            var byCanonical = new CdnVersionInfo { RevokedClientCanonicalHashes = [CanonicalHashOf(original)] };
            var result = await Verify(repacked, byCanonical);
            Assert.Equal(LocalArchiveOutcome.Rejected, result.Outcome);
            Assert.Equal(ClientRevocation.RejectionMessage, result.RejectionReason);
        }
        finally
        {
            File.Delete(original);
            File.Delete(repacked);
        }
    }

    [Fact]
    public async Task SignedArchive_RevokedByVersion_IsRejected()
    {
        string archive = CopyFixture();
        try
        {
            var result = await Verify(archive, new CdnVersionInfo { RevokedClientVersions = [FixtureVersion] });
            Assert.Equal(LocalArchiveOutcome.Rejected, result.Outcome);
            Assert.Equal(ClientRevocation.RejectionMessage, result.RejectionReason);
        }
        finally { File.Delete(archive); }
    }

    [Fact]
    public async Task SignedArchive_RevokedByFileHash_IsStillRejected()
    {
        string archive = CopyFixture();
        try
        {
            string fileHash = await FileHashHelper.ComputeSha256Async(archive, CancellationToken.None);
            var result = await Verify(archive, new CdnVersionInfo { RevokedClientHashes = [fileHash] });
            Assert.Equal(LocalArchiveOutcome.Rejected, result.Outcome);
        }
        finally { File.Delete(archive); }
    }

    [Fact]
    public async Task SignedArchive_NotRevoked_IsAccepted()
    {
        string archive = CopyFixture();
        try
        {
            var result = await Verify(archive, new CdnVersionInfo { RevokedClientVersions = ["1.0.0"] });
            Assert.Equal(LocalArchiveOutcome.Offline, result.Outcome);
            Assert.Equal(FixtureVersion, result.Version);
        }
        finally { File.Delete(archive); }
    }

    [Fact]
    public async Task UnsignedHistoricalArchive_RevokedByVersion_IsRejected()
    {
        string source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "client-archive-unsigned-sample.zip");
        string fileHash = await FileHashHelper.ComputeSha256Async(source, CancellationToken.None);
        var historical = new List<HistoricalClientArchive> { new() { Version = "4.4.2", Sha256 = fileHash } };

        var accepted = await LocalArchiveVerifier.VerifyAsync(source,
            _ => Task.FromResult<CdnVersionInfo?>(new CdnVersionInfo { HistoricalClientArchives = historical }),
            CancellationToken.None);
        Assert.Equal(LocalArchiveOutcome.Historical, accepted.Outcome);

        var revoked = await LocalArchiveVerifier.VerifyAsync(source,
            _ => Task.FromResult<CdnVersionInfo?>(new CdnVersionInfo
            {
                HistoricalClientArchives = historical,
                RevokedClientVersions = ["4.4.2"],
            }),
            CancellationToken.None);
        Assert.Equal(LocalArchiveOutcome.Rejected, revoked.Outcome);
        Assert.Equal(ClientRevocation.RejectionMessage, revoked.RejectionReason);
    }
}
