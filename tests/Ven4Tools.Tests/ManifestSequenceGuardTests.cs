using System.Text;
using System.Text.Json;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Защита от повтора старого подписанного version.json (manifest_seq): наибольший
/// принятый номер хранится в данных лаунчера, меньший номер — отказ.
/// </summary>
public sealed class ManifestSequenceGuardTests : IDisposable
{
    private readonly TemporaryDirectory _dir = new();

    private string StorePath => Path.Combine(_dir.Path, "Launcher", ManifestSequenceGuard.FileName);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void NoSequenceEver_ManifestWithoutField_IsAccepted()
    {
        var guard = new ManifestSequenceGuard(StorePath);
        Assert.True(guard.TryAccept(null, out var reason));
        Assert.Null(reason);
        Assert.Null(guard.Highest);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void IncreasingAndEqual_AreAccepted_LowerIsRejected()
    {
        var guard = new ManifestSequenceGuard(StorePath);
        Assert.True(guard.TryAccept(100, out _));
        Assert.True(guard.TryAccept(100, out _));   // тот же манифест повторно — не повтор старого
        Assert.True(guard.TryAccept(200, out _));
        Assert.False(guard.TryAccept(150, out var reason));
        Assert.Contains("150", reason);
        Assert.Equal(200, guard.Highest);
    }

    [Fact]
    public void AfterFirstSequence_ManifestWithoutField_IsRejected()
    {
        var guard = new ManifestSequenceGuard(StorePath);
        Assert.True(guard.TryAccept(100, out _));
        Assert.False(guard.TryAccept(null, out var reason));
        Assert.Contains("manifest_seq", reason);
    }

    [Fact]
    public void HighestValue_SurvivesRestart()
    {
        Assert.True(new ManifestSequenceGuard(StorePath).TryAccept(1_800_000_000, out _));

        var afterRestart = new ManifestSequenceGuard(StorePath);
        Assert.Equal(1_800_000_000, afterRestart.Highest);
        Assert.False(afterRestart.TryAccept(1_700_000_000, out _));
        Assert.False(afterRestart.TryAccept(null, out _));
    }

    [Fact]
    public void CorruptedStore_BehavesAsNoSequenceYet()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, "мусор", Encoding.UTF8);

        var guard = new ManifestSequenceGuard(StorePath);
        Assert.Null(guard.Highest);
        Assert.True(guard.TryAccept(null, out _));
        Assert.True(guard.TryAccept(5, out _));
        Assert.Equal("5", File.ReadAllText(StorePath).Trim());
    }

    [Fact]
    public void Deserialize_ReadsManifestSeq_AndOldManifestHasNone()
    {
        var withSeq = JsonSerializer.Deserialize<CdnVersionInfo>("""{ "manifest_seq": 1793952000 }""")!;
        Assert.Equal(1793952000, withSeq.ManifestSeq);
        var old = JsonSerializer.Deserialize<CdnVersionInfo>("""{ "client": { "version": "7.0.0" } }""")!;
        Assert.Null(old.ManifestSeq);
    }

    // Настоящий подписанный манифест-образец (без manifest_seq): до первого номера
    // принимается, после — отвергается как повтор, хотя подпись верна.
    [Fact]
    public void SignedManifestWithoutSeq_RejectedOnceSequenceSeen()
    {
        string json = File.ReadAllText(Fixture("version-manifest-sample.json"), Encoding.UTF8);
        string signature = File.ReadAllText(Fixture("version-manifest-sample.json.sig"), Encoding.UTF8);
        var verified = CdnService.ParseVerified(json, signature);
        Assert.NotNull(verified);

        var guard = new ManifestSequenceGuard(StorePath);
        Assert.Same(verified, CdnService.AcceptFresh(verified, guard, out var none));
        Assert.Null(none);

        Assert.True(guard.TryAccept(1793952000, out _));
        Assert.Null(CdnService.AcceptFresh(verified, guard, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void AcceptFresh_UnverifiedManifest_DoesNotTouchGuard()
    {
        var guard = new ManifestSequenceGuard(StorePath);
        Assert.Null(CdnService.AcceptFresh(null, guard, out var reason));
        Assert.Null(reason);
        Assert.Null(guard.Highest);
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
