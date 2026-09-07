using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Слепок распакованного каталога — единственная защита файлов между распаковкой
/// архива и их переносом в папку клиента: SHA256 архива подтверждает архив, а не
/// то, что лежит в staging после произвольно долгого ожидания «закройте клиент».
/// Проверяется чистая файловая логика на временном каталоге — ни распаковки, ни
/// реального клиента здесь не нужно.
/// </summary>
public sealed class StagingIntegritySnapshotTests
{
    private static void WriteFile(string root, string relative, string content)
    {
        string full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static async Task<StagingIntegritySnapshot> FillAndCaptureAsync(string root)
    {
        WriteFile(root, "Ven4Tools.exe", "исполняемый файл");
        WriteFile(root, "Data/master.json", "каталог");
        WriteFile(root, "runtimes/win-x64/native/lib.dll", "библиотека");
        return await StagingIntegritySnapshot.CaptureAsync(root, CancellationToken.None);
    }

    [Fact]
    public async Task FindDifference_ReturnsNullWhenNothingChanged()
    {
        using var area = new TemporaryDirectory();
        var snapshot = await FillAndCaptureAsync(area.Path);

        Assert.Equal(3, snapshot.FileCount);
        Assert.Null(await snapshot.FindDifferenceAsync(area.Path, CancellationToken.None));
    }

    [Fact]
    public async Task FindDifference_DetectsReplacedFileContent()
    {
        using var area = new TemporaryDirectory();
        var snapshot = await FillAndCaptureAsync(area.Path);

        // Классическая подмена: файл того же имени в каталоге, доступном на запись
        // процессу того же пользователя, пока лаунчер ждёт закрытия клиента.
        WriteFile(area.Path, "Ven4Tools.exe", "подменённый файл");

        string? difference = await snapshot.FindDifferenceAsync(area.Path, CancellationToken.None);
        Assert.NotNull(difference);
        Assert.Contains("изменился файл", difference);
        Assert.Contains("Ven4Tools.exe", difference);
    }

    [Fact]
    public async Task FindDifference_DetectsChangeOfSameLength()
    {
        // Размер совпадает — расхождение видно только по хешу.
        using var area = new TemporaryDirectory();
        WriteFile(area.Path, "app.dll", "aaaa");
        var snapshot = await StagingIntegritySnapshot.CaptureAsync(area.Path, CancellationToken.None);

        WriteFile(area.Path, "app.dll", "bbbb");

        string? difference = await snapshot.FindDifferenceAsync(area.Path, CancellationToken.None);
        Assert.NotNull(difference);
        Assert.Contains("изменился файл", difference);
    }

    [Fact]
    public async Task FindDifference_DetectsAddedFileInNestedDirectory()
    {
        // Подложенная рядом dll не меняет ни один существующий файл, но после
        // установки окажется в папке клиента и может быть подгружена рядом с exe.
        using var area = new TemporaryDirectory();
        var snapshot = await FillAndCaptureAsync(area.Path);

        WriteFile(area.Path, "runtimes/win-x64/native/evil.dll", "чужой код");

        string? difference = await snapshot.FindDifferenceAsync(area.Path, CancellationToken.None);
        Assert.NotNull(difference);
        Assert.Contains("посторонний файл", difference);
        Assert.Contains("evil.dll", difference);
    }

    [Fact]
    public async Task FindDifference_DetectsRemovedFile()
    {
        using var area = new TemporaryDirectory();
        var snapshot = await FillAndCaptureAsync(area.Path);

        File.Delete(Path.Combine(area.Path, "Data", "master.json"));

        string? difference = await snapshot.FindDifferenceAsync(area.Path, CancellationToken.None);
        Assert.NotNull(difference);
        Assert.Contains("исчез файл", difference);
        Assert.Contains("Data/master.json", difference);
    }

    [Fact]
    public async Task FindDifference_ReportsMissingDirectoryInsteadOfThrowing()
    {
        // Каталог целиком унесли, пока лаунчер ждал пользователя. Это расхождение,
        // а не падение установки с непонятным исключением.
        using var area = new TemporaryDirectory();
        string staging = Path.Combine(area.Path, "staging");
        Directory.CreateDirectory(staging);
        WriteFile(staging, "app.dll", "файл");
        var snapshot = await StagingIntegritySnapshot.CaptureAsync(staging, CancellationToken.None);

        Directory.Delete(staging, recursive: true);

        string? difference = await snapshot.FindDifferenceAsync(staging, CancellationToken.None);
        Assert.NotNull(difference);
        Assert.Contains("каталог распаковки исчез", difference);
    }

    [Fact]
    public async Task Capture_ThrowsWhenDirectoryMissing()
    {
        using var area = new TemporaryDirectory();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => StagingIntegritySnapshot.CaptureAsync(
                Path.Combine(area.Path, "нет-такого"), CancellationToken.None));
    }

    [Fact]
    public async Task FindDifference_ReturnsNullForEmptyDirectory()
    {
        using var area = new TemporaryDirectory();
        var snapshot = await StagingIntegritySnapshot.CaptureAsync(area.Path, CancellationToken.None);

        Assert.Equal(0, snapshot.FileCount);
        Assert.Null(await snapshot.FindDifferenceAsync(area.Path, CancellationToken.None));
    }
}
