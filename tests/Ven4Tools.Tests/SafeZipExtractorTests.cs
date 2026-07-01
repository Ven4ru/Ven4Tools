using System.IO.Compression;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class SafeZipExtractorTests
{
    [Fact]
    public async Task ExtractAsync_ExtractsRegularNestedFiles()
    {
        using var area = new TemporaryDirectory();
        string archivePath = Path.Combine(area.Path, "valid.zip");
        string destination = Path.Combine(area.Path, "staging");
        CreateArchive(archivePath, ("bin/Ven4Tools.exe", "payload"));

        await SafeZipExtractor.ExtractAsync(archivePath, destination, CancellationToken.None);

        Assert.Equal(
            "payload",
            await File.ReadAllTextAsync(Path.Combine(destination, "bin", "Ven4Tools.exe")));
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("folder/../../escaped.txt")]
    [InlineData("C:/escaped.txt")]
    [InlineData("/escaped.txt")]
    public async Task ExtractAsync_RejectsPathsOutsideStaging(string entryName)
    {
        using var area = new TemporaryDirectory();
        string archivePath = Path.Combine(area.Path, "traversal.zip");
        string destination = Path.Combine(area.Path, "staging");
        CreateArchive(archivePath, (entryName, "blocked"));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => SafeZipExtractor.ExtractAsync(archivePath, destination, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(area.Path, "escaped.txt")));
    }

    [Fact]
    public async Task ExtractAsync_RejectsCorruptArchive()
    {
        using var area = new TemporaryDirectory();
        string archivePath = Path.Combine(area.Path, "corrupt.zip");
        await File.WriteAllTextAsync(archivePath, "not a zip");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => SafeZipExtractor.ExtractAsync(
                archivePath,
                Path.Combine(area.Path, "staging"),
                CancellationToken.None));
    }

    [Fact]
    public async Task ExtractAsync_RejectsSymbolicLink()
    {
        using var area = new TemporaryDirectory();
        string archivePath = Path.Combine(area.Path, "symlink.zip");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("link");
            entry.ExternalAttributes = (0xA000 | 0x1FF) << 16;
            using StreamWriter writer = new(entry.Open());
            writer.Write("../outside");
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            () => SafeZipExtractor.ExtractAsync(
                archivePath,
                Path.Combine(area.Path, "staging"),
                CancellationToken.None));
    }

    [Fact]
    public async Task ExtractAsync_RejectsContaminatedStaging()
    {
        using var area = new TemporaryDirectory();
        string archivePath = Path.Combine(area.Path, "valid.zip");
        string destination = Path.Combine(area.Path, "staging");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "foreign.file"), "foreign");
        CreateArchive(archivePath, ("Ven4Tools.exe", "payload"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SafeZipExtractor.ExtractAsync(
                archivePath,
                destination,
                CancellationToken.None));
    }

    [Fact]
    public async Task ExtractAsync_HonorsCancellation()
    {
        using var area = new TemporaryDirectory();
        string archivePath = Path.Combine(area.Path, "valid.zip");
        CreateArchive(archivePath, ("Ven4Tools.exe", "payload"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SafeZipExtractor.ExtractAsync(
                archivePath,
                Path.Combine(area.Path, "staging"),
                cancellation.Token));
    }

    private static void CreateArchive(
        string archivePath,
        params (string Name, string Content)[] entries)
    {
        using ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach ((string name, string content) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name);
            using StreamWriter writer = new(entry.Open());
            writer.Write(content);
        }
    }
}
