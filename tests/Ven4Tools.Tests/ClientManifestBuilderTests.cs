using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Построение манифеста по реальной папке на диске. Отдельный акцент — исключение
/// файлов, которые не входят в публикацию, но реально появляются в папке клиента:
/// без этого «Проверка и восстановление клиента» (использующая тот же построитель)
/// считала бы собственный кэш клиента «лишним файлом» и удаляла бы его у каждого
/// пользователя, хотя это не повреждение, а нормальная работа приложения.
/// </summary>
public sealed class ClientManifestBuilderTests : IDisposable
{
    private readonly string _root;

    public ClientManifestBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"Ven4Tools_ManifestBuilderTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* временная папка теста */ }
    }

    private void WriteFile(string relativePath, string content)
    {
        string full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task BuildFromDirectoryAsync_IncludesRealPublicationFiles()
    {
        WriteFile("Ven4Tools.exe", "exe");
        WriteFile("Ven4Tools.dll", "dll");

        var manifest = await ClientManifestBuilder.BuildFromDirectoryAsync(_root, "5.0.0", CancellationToken.None);

        Assert.Equal("5.0.0", manifest.Version);
        Assert.Equal(
            new[] { "Ven4Tools.dll", "Ven4Tools.exe" },
            manifest.Files!.Select(f => f.Path));
    }

    [Fact]
    public async Task BuildFromDirectoryAsync_ExcludesClientOwnCatalogCache()
    {
        // Ven4Tools/Services/CatalogLoaderService.cs пишет сюда кэш каталога во время
        // работы клиента (AppDomain.CurrentDomain.BaseDirectory, безусловно) — это не
        // часть публикации и никогда не попадёт в подписанный client-manifest.json.
        WriteFile("Ven4Tools.exe", "exe");
        WriteFile("Data/master.json", "{}");
        WriteFile("Data/master.json.sig", "подпись");

        var manifest = await ClientManifestBuilder.BuildFromDirectoryAsync(_root, "5.0.0", CancellationToken.None);

        Assert.Equal(new[] { "Ven4Tools.exe" }, manifest.Files!.Select(f => f.Path));
    }

    [Fact]
    public async Task BuildFromDirectoryAsync_ExcludesTransientPartialInstallArtifacts()
    {
        WriteFile("Ven4Tools.exe", "exe");
        WriteFile($"Ven4Tools.dll.new-{Guid.NewGuid():N}", "недоставленный файл");
        WriteFile($"Ven4Tools.dll.old-{Guid.NewGuid():N}", "резервная копия отката");

        var manifest = await ClientManifestBuilder.BuildFromDirectoryAsync(_root, "5.0.0", CancellationToken.None);

        Assert.Equal(new[] { "Ven4Tools.exe" }, manifest.Files!.Select(f => f.Path));
    }
}
