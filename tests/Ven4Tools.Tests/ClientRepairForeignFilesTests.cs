using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Находка ревью 05.11: «Исправить» удалял всё, чего нет в манифесте, — и чужие файлы,
/// если папку клиента привязали вручную. И перенос при доделке после обрыва удалял
/// старую папку, не глядя на посторонние файлы.
/// </summary>
public sealed class ClientRepairForeignFilesTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    // Без deps.json: свои файлы корня — Ven4Tools.* и сборки по расширению.
    private static readonly ClientFolderInspector NoDeps = ClientFolderInspector.FromListedFiles(null);

    [Theory]
    // Похоже на файл публикации — удаляется.
    [InlineData("intruder.dll", true)]
    [InlineData("version.dll", true)]
    [InlineData("stale.json", true)]
    [InlineData("ru/Ven4Tools.resources.dll", true)]
    [InlineData("Data/old-cache.json", true)]
    [InlineData("Personalization/old.wav", true)]
    [InlineData("Personalization/sub/old.ani", true)]
    [InlineData("runtimes/win-x64/native/x.dll", true)]
    [InlineData("createdump.exe", true)]
    // Не похоже на файл клиента — остаётся.
    [InlineData("notes.txt", false)]
    [InlineData("Отчёт.docx", false)]
    [InlineData("backup.zip", false)]
    [InlineData("Data/мой список.txt", false)]
    // Чужая подпапка — остаётся целиком, даже dll и exe.
    [InlineData("7-Zip/7z.dll", false)]
    [InlineData("Games/game.exe", false)]
    [InlineData("Мои программы/settings.json", false)]
    // Чужой exe в корне (portable-программа рядом с клиентом) — остаётся.
    [InlineData("putty.exe", false)]
    [InlineData("screensaver.scr", false)]
    public void IsDeletable(string path, bool expected)
    {
        Assert.Equal(expected, ClientRepairDeletionPolicy.IsDeletable(path, NoDeps));
    }

    [Fact]
    public void Split_KeepsOrderAndSeparatesLists()
    {
        var (delete, keep) = ClientRepairDeletionPolicy.Split(
            new[] { "a.dll", "notes.txt", "Tools/x.dll", "ru/b.dll" }, NoDeps);

        Assert.Equal(new[] { "a.dll", "ru/b.dll" }, delete);
        Assert.Equal(new[] { "notes.txt", "Tools/x.dll" }, keep);
    }

    [Fact]
    public void PublicationExtensions_CoverRealPublicationTypes()
    {
        // Состав установленного клиента 7.x: сборки, json, файлы оформления Personalization.
        foreach (string ext in new[] { ".dll", ".exe", ".json", ".pdb", ".wav", ".cur", ".ani", ".jpg", ".scr", ".sig" })
            Assert.Contains(ext, ClientRepairDeletionPolicy.PublicationExtensions);
        Assert.DoesNotContain(".txt", ClientRepairDeletionPolicy.PublicationExtensions);
        Assert.DoesNotContain(".docx", ClientRepairDeletionPolicy.PublicationExtensions);
    }

    // --- Проверка целостности: чужое не попадает в план удаления ---

    private sealed class FixedEnvironment(ClientFileManifest local, ClientFileManifest remote) : IClientIntegrityEnvironment
    {
        public bool ClientExecutableExists(string clientPath) => true;
        public Task<ClientFileManifest> BuildLocalManifestAsync(string clientPath, string versionLabel, CancellationToken cancellationToken) =>
            Task.FromResult(local);
        public Task<ClientFileManifest?> FetchRemoteManifestAsync(string manifestUrl, string signatureUrl, CancellationToken cancellationToken) =>
            Task.FromResult<ClientFileManifest?>(remote);
        public bool IsAclCompromised(string clientPath) => false;
    }

    private static ClientFileManifest Manifest(params (string Path, string Hash)[] files) => new()
    {
        Version = "7.0.0",
        Files = files.Select(f => new ClientManifestFileEntry { Path = f.Path, Sha256 = f.Hash, Size = 1 }).ToList(),
    };

    private static ClientIntegritySources Sources() => new()
    {
        ManifestUrl = "https://cdn.ven4tools.ru/client-files/7.0.0/client-manifest.json",
        ManifestSignatureUrl = "https://cdn.ven4tools.ru/client-files/7.0.0/client-manifest.json.sig",
        FilesBaseUrl = "https://cdn.ven4tools.ru/client-files/7.0.0/",
    };

    private static readonly (string, string)[] Publication =
    [
        ("Ven4Tools.exe", HashA), ("Ven4Tools.dll", HashA), ("ru/Ven4Tools.resources.dll", HashA), ("Data/x.json", HashA),
    ];

    [Fact]
    public async Task Check_ForeignFilesInBoundFolder_AreKeptAndListed()
    {
        var local = Manifest(Publication.Concat(new[]
        {
            ("intruder.dll", HashB),
            ("notes.txt", HashB),
            ("7-Zip/7z.dll", HashB),
            ("putty.exe", HashB),
        }).ToArray());
        var checker = new ClientIntegrityChecker(new FixedEnvironment(local, Manifest(Publication)), repairExecutor: null);

        var report = await checker.CheckAsync(@"C:\Нет такой папки\Ven4Tools_Client", "7.0.0", Sources(), CancellationToken.None);

        Assert.Equal(ClientIntegrityStatus.RepairAvailable, report.Status);
        Assert.Equal(new[] { "intruder.dll" }, report.Plan!.ToDelete);
        Assert.Equal(new[] { "notes.txt", "7-Zip/7z.dll", "putty.exe" }, report.KeptForeignFiles);
        Assert.Contains("не трогаю: 3", report.Summary);
    }

    [Fact]
    public async Task Check_OnlyForeignExtras_IsHealthy()
    {
        var local = Manifest(Publication.Concat(new[] { ("Документы/план.txt", HashB) }).ToArray());
        var checker = new ClientIntegrityChecker(new FixedEnvironment(local, Manifest(Publication)), repairExecutor: null);

        var report = await checker.CheckAsync(@"C:\Нет такой папки\Ven4Tools_Client", "7.0.0", Sources(), CancellationToken.None);

        Assert.Equal(ClientIntegrityStatus.Healthy, report.Status);
        Assert.False(report.HasRepairableFindings);
        Assert.Equal(new[] { "Документы/план.txt" }, report.KeptForeignFiles);
    }

    // --- Перенос: старая папка с посторонними файлами не удаляется ---

    private static void MakeClient(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, LauncherPaths.ClientExeName), "exe");
        File.WriteAllText(Path.Combine(folder, "Ven4Tools.dll"), "dll");
    }

    [Fact]
    public async Task RemoveOldCopy_WithForeignFiles_KeepsFolder()
    {
        using var root = new TemporaryDirectory();
        string oldClient = Path.Combine(root.Path, "Старая", LauncherPaths.ClientFolderName);
        string newClient = Path.Combine(root.Path, "Новая", LauncherPaths.ClientFolderName);
        MakeClient(oldClient);
        MakeClient(newClient);
        File.WriteAllText(Path.Combine(oldClient, "мои заметки.txt"), "чужое");

        var (result, foreign) = await ClientFolderMover.TryRemoveOldCopyAsync(
            oldClient, newClient, Path.Combine(root.Path, "data"));

        Assert.Equal(OldCopyRemoval.KeptForeign, result);
        Assert.Contains("мои заметки.txt", foreign);
        Assert.True(File.Exists(Path.Combine(oldClient, "мои заметки.txt")));
        Assert.True(File.Exists(Path.Combine(oldClient, LauncherPaths.ClientExeName)));
    }

    [Fact]
    public async Task RemoveOldCopy_CleanClientFolder_IsRemoved()
    {
        using var root = new TemporaryDirectory();
        string oldClient = Path.Combine(root.Path, "Старая", LauncherPaths.ClientFolderName);
        string newClient = Path.Combine(root.Path, "Новая", LauncherPaths.ClientFolderName);
        MakeClient(oldClient);
        MakeClient(newClient);

        var (result, foreign) = await ClientFolderMover.TryRemoveOldCopyAsync(
            oldClient, newClient, Path.Combine(root.Path, "data"));

        Assert.Equal(OldCopyRemoval.Removed, result);
        Assert.Empty(foreign);
        Assert.False(Directory.Exists(oldClient));
        Assert.True(Directory.Exists(newClient));
    }

    [Fact]
    public async Task RemoveOldCopy_NewCopyMissing_NotRemoved()
    {
        using var root = new TemporaryDirectory();
        string oldClient = Path.Combine(root.Path, "Старая", LauncherPaths.ClientFolderName);
        MakeClient(oldClient);

        var (result, _) = await ClientFolderMover.TryRemoveOldCopyAsync(
            oldClient, Path.Combine(root.Path, "Новая", LauncherPaths.ClientFolderName), Path.Combine(root.Path, "data"));

        Assert.Equal(OldCopyRemoval.NotRemoved, result);
        Assert.True(Directory.Exists(oldClient));
    }
}
