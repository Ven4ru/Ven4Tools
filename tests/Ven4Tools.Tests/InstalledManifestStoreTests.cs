using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Локальный кэш состава установленной версии клиента. Главное требование —
/// отказоустойчивость: отсутствующий или испорченный файл обязан читаться как
/// «кэша нет» (null), а не бросать исключение. Обновление клиента не должно
/// падать из-за вспомогательного файла, без которого прекрасно работает полный
/// путь загрузки.
/// </summary>
public sealed class InstalledManifestStoreTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static ClientFileManifest SampleManifest() => new()
    {
        Version = "5.1.0",
        GeneratedAt = "2026-09-02T12:00:00Z",
        Files =
        [
            new ClientManifestFileEntry { Path = "Ven4Tools.exe", Sha256 = Hash, Size = 253440 },
            new ClientManifestFileEntry { Path = "Resources/тема.xaml", Sha256 = Hash, Size = 42 },
        ],
    };

    [Fact]
    public void SaveThenLoad_ReturnsSameManifest()
    {
        using var area = new TemporaryDirectory();
        var store = new InstalledManifestStore(Path.Combine(area.Path, "nested", "installed.json"));

        Assert.True(store.Save(SampleManifest()));
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("5.1.0", loaded!.Version);
        Assert.Equal(2, loaded.Files!.Count);
        // Кириллица в пути файла не должна пострадать при записи/чтении.
        Assert.Equal("Resources/тема.xaml", loaded.Files[1].Path);
        Assert.Equal(253440, loaded.Files[0].Size);
    }

    [Fact]
    public void Load_ReturnsNullWhenFileMissing()
    {
        using var area = new TemporaryDirectory();
        var store = new InstalledManifestStore(Path.Combine(area.Path, "нет-такого.json"));

        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_ReturnsNullWhenJsonIsCorrupted()
    {
        using var area = new TemporaryDirectory();
        string path = Path.Combine(area.Path, "installed.json");
        File.WriteAllText(path, "{ это не json");

        Assert.Null(new InstalledManifestStore(path).Load());
    }

    [Fact]
    public void Load_ReturnsNullWhenManifestHasNoFiles()
    {
        using var area = new TemporaryDirectory();
        string path = Path.Combine(area.Path, "installed.json");
        File.WriteAllText(path, """{"version":"5.1.0","files":[]}""");

        Assert.Null(new InstalledManifestStore(path).Load());
    }

    [Fact]
    public void Save_OverwritesPreviousManifest()
    {
        using var area = new TemporaryDirectory();
        string path = Path.Combine(area.Path, "installed.json");
        var store = new InstalledManifestStore(path);

        store.Save(SampleManifest());
        var next = SampleManifest();
        next.Version = "5.2.0";
        next.Files!.RemoveAt(1);
        Assert.True(store.Save(next));

        var loaded = store.Load();
        Assert.Equal("5.2.0", loaded!.Version);
        Assert.Single(loaded.Files!);
        // Временный файл записи не должен оставаться рядом с кэшем.
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Invalidate_RemovesManifestAndIsSafeWhenAlreadyAbsent()
    {
        using var area = new TemporaryDirectory();
        string path = Path.Combine(area.Path, "installed.json");
        var store = new InstalledManifestStore(path);
        store.Save(SampleManifest());

        store.Invalidate();
        Assert.Null(store.Load());

        store.Invalidate(); // повторный вызов не должен бросать
        Assert.Null(store.Load());
    }

    [Fact]
    public void DefaultPath_PointsToLauncherDataFolder()
    {
        // Кэш намеренно лежит вне папки клиента: дельта точечно перезаписывает и
        // удаляет её содержимое, и метаданные о составе там же были бы под ударом
        // того самого процесса, который они описывают.
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "Launcher", InstalledManifestStore.FileName);

        Assert.Equal(expected, InstalledManifestStore.DefaultPath);
    }
}
