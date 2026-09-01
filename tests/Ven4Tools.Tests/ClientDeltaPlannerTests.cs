using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Ядро блочного (дельта-) обновления клиента: сравнение манифеста новой версии
/// с манифестом установленной. Чистая функция без сети и диска — поэтому именно
/// здесь имеет смысл держать основное покрытие всей функции.
/// </summary>
public sealed class ClientDeltaPlannerTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string HashC = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private static ClientFileManifest Manifest(params (string Path, string Hash)[] files) =>
        new()
        {
            Version = "5.1.0",
            Files = files
                .Select(f => new ClientManifestFileEntry { Path = f.Path, Sha256 = f.Hash, Size = 100 })
                .ToList(),
        };

    [Fact]
    public void Plan_DownloadsOnlyChangedAndNewFiles()
    {
        // Реалистичное соотношение "сотни файлов рантайма не меняются, единицы меняются"
        // (см. ClientDeltaPlanner.MinimumUnchangedShare) — четыре дополнительных
        // неизменившихся рантайм-файла нужны, чтобы доля совпадений (5 из 7 ≈ 71%)
        // прошла 50%-й порог; без них тест проверял бы неизбежное "дельта невыгодна"
        // на нереалистично маленьком манифесте, а не саму логику диффа.
        var remote = Manifest(
            ("Ven4Tools.exe", HashA),
            ("Ven4Tools.dll", HashB),   // изменился
            ("Resources/new.dat", HashC), // новый
            ("PresentationFramework.dll", HashA),
            ("System.Private.CoreLib.dll", HashA),
            ("coreclr.dll", HashA),
            ("hostfxr.dll", HashA));
        var local = Manifest(
            ("Ven4Tools.exe", HashA),
            ("Ven4Tools.dll", HashA),
            ("PresentationFramework.dll", HashA),
            ("System.Private.CoreLib.dll", HashA),
            ("coreclr.dll", HashA),
            ("hostfxr.dll", HashA));

        var plan = ClientDeltaPlanner.Plan(remote, local);

        Assert.False(plan.FullDownloadRecommended);
        Assert.Equal(
            new[] { "Ven4Tools.dll", "Resources/new.dat" },
            plan.ToDownload.Select(e => e.Path));
        Assert.Equal(
            new[] { "Ven4Tools.exe", "PresentationFramework.dll", "System.Private.CoreLib.dll", "coreclr.dll", "hostfxr.dll" },
            plan.Unchanged.Select(e => e.Path));
        Assert.Empty(plan.ToDelete);
        Assert.Equal(200, plan.DownloadBytes);
    }

    [Fact]
    public void Plan_MarksFilesMissingFromNewVersionForDeletion()
    {
        var remote = Manifest(("a.dll", HashA), ("b.dll", HashA), ("c.dll", HashA));
        var local = Manifest(("a.dll", HashA), ("b.dll", HashA), ("c.dll", HashA), ("legacy.dll", HashB));

        var plan = ClientDeltaPlanner.Plan(remote, local);

        Assert.False(plan.FullDownloadRecommended);
        Assert.Empty(plan.ToDownload);
        Assert.Equal(new[] { "legacy.dll" }, plan.ToDelete);
    }

    [Fact]
    public void Plan_TreatsIdenticalManifestsAsEmptyDelta()
    {
        var manifest = Manifest(("a.dll", HashA), ("b.dll", HashB));

        var plan = ClientDeltaPlanner.Plan(manifest, manifest);

        Assert.False(plan.FullDownloadRecommended);
        Assert.Empty(plan.ToDownload);
        Assert.Empty(plan.ToDelete);
        Assert.Equal(2, plan.Unchanged.Count);
    }

    [Fact]
    public void Plan_ComparesPathsCaseInsensitively()
    {
        // На файловой системе Windows «Ven4Tools.dll» и «ven4tools.dll» — один и тот
        // же файл. Ordinal-сравнение породило бы план, в котором он одновременно
        // скачивается и удаляется.
        var remote = Manifest(("Ven4Tools.dll", HashA), ("other.dll", HashB));
        var local = Manifest(("ven4tools.DLL", HashA), ("other.dll", HashB));

        var plan = ClientDeltaPlanner.Plan(remote, local);

        Assert.False(plan.FullDownloadRecommended);
        Assert.Empty(plan.ToDownload);
        Assert.Empty(plan.ToDelete);
    }

    [Fact]
    public void Plan_RecommendsFullDownloadWhenLocalManifestMissing()
    {
        var plan = ClientDeltaPlanner.Plan(Manifest(("a.dll", HashA)), null);

        Assert.True(plan.FullDownloadRecommended);
        Assert.Contains("нет локального манифеста", plan.Reason);
    }

    [Fact]
    public void Plan_RecommendsFullDownloadWhenLocalManifestHasNoFiles()
    {
        // Битый JSON InstalledManifestStore отдаёт как null, но пустой список файлов
        // мог остаться и от корректного разбора — это то же «сравнивать не с чем».
        var plan = ClientDeltaPlanner.Plan(Manifest(("a.dll", HashA)), new ClientFileManifest());

        Assert.True(plan.FullDownloadRecommended);
    }

    [Fact]
    public void Plan_RecommendsFullDownloadWhenLessThanHalfOfFilesMatch()
    {
        // 4 из 10 совпало — 40%, ниже порога выгодности.
        var remote = Manifest(Enumerable.Range(0, 10)
            .Select(i => ($"file{i}.dll", i < 4 ? HashA : HashB)).ToArray());
        var local = Manifest(Enumerable.Range(0, 10)
            .Select(i => ($"file{i}.dll", i < 4 ? HashA : HashC)).ToArray());

        var plan = ClientDeltaPlanner.Plan(remote, local);

        Assert.True(plan.FullDownloadRecommended);
        Assert.Contains("невыгодна", plan.Reason);
    }

    [Fact]
    public void Plan_AllowsDeltaExactlyAtThreshold()
    {
        // Ровно 50% — порог «меньше половины», а не «меньше или равно».
        var remote = Manifest(Enumerable.Range(0, 10)
            .Select(i => ($"file{i}.dll", i < 5 ? HashA : HashB)).ToArray());
        var local = Manifest(Enumerable.Range(0, 10)
            .Select(i => ($"file{i}.dll", i < 5 ? HashA : HashC)).ToArray());

        var plan = ClientDeltaPlanner.Plan(remote, local);

        Assert.False(plan.FullDownloadRecommended);
        Assert.Equal(5, plan.ToDownload.Count);
    }

    [Fact]
    public void Plan_RecommendsFullDownloadWhenRemoteManifestEmpty()
    {
        Assert.True(ClientDeltaPlanner.Plan(null, Manifest(("a.dll", HashA))).FullDownloadRecommended);
        Assert.True(ClientDeltaPlanner.Plan(new ClientFileManifest(), Manifest(("a.dll", HashA))).FullDownloadRecommended);
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("..\\evil.dll")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/System32/evil.dll")]
    [InlineData("sub/../../evil.dll")]
    [InlineData("")]
    public void Plan_RejectsUnsafePathsInRemoteManifest(string path)
    {
        // Подпись манифеста не отменяет проверку пути: именно здесь строка из сети
        // превращается в путь записи на диск (тот же класс защиты, что zip-slip).
        var remote = Manifest((path, HashA), ("ok.dll", HashB));
        var local = Manifest(("ok.dll", HashB));

        var plan = ClientDeltaPlanner.Plan(remote, local);

        Assert.True(plan.FullDownloadRecommended);
    }

    [Fact]
    public void Plan_RejectsInvalidHashInRemoteManifest()
    {
        var remote = Manifest(("a.dll", "не-хеш"), ("ok.dll", HashB));
        var local = Manifest(("ok.dll", HashB));

        Assert.True(ClientDeltaPlanner.Plan(remote, local).FullDownloadRecommended);
    }

    [Fact]
    public void Plan_RejectsNegativeSizeInRemoteManifest()
    {
        var remote = new ClientFileManifest
        {
            Version = "5.1.0",
            Files = [new ClientManifestFileEntry { Path = "a.dll", Sha256 = HashA, Size = -1 }],
        };

        Assert.True(ClientDeltaPlanner.Plan(remote, Manifest(("a.dll", HashA))).FullDownloadRecommended);
    }

    [Fact]
    public void Plan_IgnoresLocalEntriesWithoutPathOrHash()
    {
        var remote = Manifest(("a.dll", HashA), ("b.dll", HashB));
        var local = Manifest(("a.dll", HashA), ("b.dll", HashB));
        local.Files!.Add(new ClientManifestFileEntry { Path = null, Sha256 = HashC, Size = 1 });
        local.Files.Add(new ClientManifestFileEntry { Path = "c.dll", Sha256 = null, Size = 1 });

        var plan = ClientDeltaPlanner.Plan(remote, local);

        Assert.False(plan.FullDownloadRecommended);
        Assert.Empty(plan.ToDownload);
        // Запись без хеша всё ещё описывает файл на диске — он пропал из новой
        // версии, значит подлежит удалению; запись без пути пропускается целиком.
        Assert.Equal(new[] { "c.dll" }, plan.ToDelete);
    }

    [Fact]
    public void Plan_RejectsDuplicatePathsInRemoteManifest()
    {
        var remote = Manifest(("a.dll", HashA), ("A.DLL", HashB));
        var local = Manifest(("a.dll", HashA));

        var plan = ClientDeltaPlanner.Plan(remote, local);

        Assert.True(plan.FullDownloadRecommended);
        Assert.Contains("повторяется", plan.Reason);
    }

    [Fact]
    public void Plan_SkipsUnsafePathsInLocalManifestInsteadOfFailing()
    {
        // Локальный кэш не подписан, его порча — не атака на новую версию: битую
        // запись достаточно пропустить, отменять из-за неё всю дельту незачем.
        var remote = Manifest(("a.dll", HashA), ("b.dll", HashB));
        var local = Manifest(("a.dll", HashA), ("b.dll", HashB), ("../evil.dll", HashC));

        var plan = ClientDeltaPlanner.Plan(remote, local);

        Assert.False(plan.FullDownloadRecommended);
        Assert.Empty(plan.ToDelete);
    }
}
