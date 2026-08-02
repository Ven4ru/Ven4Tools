using System.IO.Compression;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class CanonicalArchiveHasherTests
{
    private static MemoryStream BuildZip(Action<ZipArchive> populate)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            populate(archive);
        stream.Position = 0;
        return stream;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    [Fact]
    public void SameFilesDifferentInsertionOrder_ProduceSameHash()
    {
        using var streamA = BuildZip(a =>
        {
            WriteEntry(a, "b.txt", "second");
            WriteEntry(a, "a.txt", "first");
        });
        using var streamB = BuildZip(a =>
        {
            WriteEntry(a, "a.txt", "first");
            WriteEntry(a, "b.txt", "second");
        });

        using var archiveA = new ZipArchive(streamA, ZipArchiveMode.Read);
        using var archiveB = new ZipArchive(streamB, ZipArchiveMode.Read);

        Assert.Equal(CanonicalArchiveHasher.ComputeHex(archiveA), CanonicalArchiveHasher.ComputeHex(archiveB));
    }

    [Fact]
    public void SignatureEntry_IsIgnored()
    {
        using var withoutSig = BuildZip(a => WriteEntry(a, "a.txt", "content"));
        using var withSig = BuildZip(a =>
        {
            WriteEntry(a, "a.txt", "content");
            WriteEntry(a, CanonicalArchiveHasher.SignatureEntryName, "{\"anything\":true}");
        });

        using var archiveWithout = new ZipArchive(withoutSig, ZipArchiveMode.Read);
        using var archiveWith = new ZipArchive(withSig, ZipArchiveMode.Read);

        Assert.Equal(
            CanonicalArchiveHasher.ComputeHex(archiveWithout),
            CanonicalArchiveHasher.ComputeHex(archiveWith));
    }

    [Fact]
    public void OneByteDifference_ProducesDifferentHash()
    {
        using var original = BuildZip(a => WriteEntry(a, "a.txt", "content"));
        using var tampered = BuildZip(a => WriteEntry(a, "a.txt", "kontent"));

        using var archiveOriginal = new ZipArchive(original, ZipArchiveMode.Read);
        using var archiveTampered = new ZipArchive(tampered, ZipArchiveMode.Read);

        Assert.NotEqual(
            CanonicalArchiveHasher.ComputeHex(archiveOriginal),
            CanonicalArchiveHasher.ComputeHex(archiveTampered));
    }

    [Fact]
    public void DirectoryEntries_DoNotAffectHash()
    {
        using var withoutDir = BuildZip(a => WriteEntry(a, "sub/a.txt", "content"));
        using var withDir = BuildZip(a =>
        {
            a.CreateEntry("sub/"); // директория: entry.Name пусто, entry.FullName == "sub/"
            WriteEntry(a, "sub/a.txt", "content");
        });

        using var archiveWithoutDir = new ZipArchive(withoutDir, ZipArchiveMode.Read);
        using var archiveWithDir = new ZipArchive(withDir, ZipArchiveMode.Read);

        Assert.Equal(
            CanonicalArchiveHasher.ComputeHex(archiveWithoutDir),
            CanonicalArchiveHasher.ComputeHex(archiveWithDir));
    }

    [Fact]
    public void AmbiguousConcatenation_NameContentSplit_ProducesDifferentHash()
    {
        // "ab"+"" + "" (имя "ab", контент "") не должно совпасть с "a"+"b" по хешу —
        // проверяет, что имя и контент не конкатенируются без разделителя/префикса длины.
        using var variantA = BuildZip(a => WriteEntry(a, "ab", ""));
        using var variantB = BuildZip(a => WriteEntry(a, "a", "b"));

        using var archiveA = new ZipArchive(variantA, ZipArchiveMode.Read);
        using var archiveB = new ZipArchive(variantB, ZipArchiveMode.Read);

        Assert.NotEqual(
            CanonicalArchiveHasher.ComputeHex(archiveA),
            CanonicalArchiveHasher.ComputeHex(archiveB));
    }
}
