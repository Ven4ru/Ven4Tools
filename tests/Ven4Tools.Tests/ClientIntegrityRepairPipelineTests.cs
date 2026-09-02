using System.Security.Cryptography;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// «Проверить и восстановить клиент» целиком, на настоящих файлах: реальная папка
/// публикации на диске → реальный <see cref="ClientManifestBuilder"/> → реальный
/// <see cref="ClientDeltaPlanner"/> → реальный
/// <see cref="TransactionalDirectoryInstaller.InstallPartial"/> через реальный
/// <see cref="ClientDeltaInstaller"/>. Подменены ровно две вещи, которые в тесте
/// быть не могут: HTTP (эталонный манифест и файлы берутся из локальной папки
/// «публикации» вместо CDN) и ACL временного каталога.
///
/// Почему это не дублирует уже существующие наборы: TransactionalDirectoryInstallerPartialTests
/// проверяет саму транзакцию, ClientManifestBuilderTests — обход папки,
/// ClientDeltaPlannerTests — сравнение манифестов, ClientIntegrityCheckerTests —
/// выбор вердикта на подменённых входах. Каждый кусок покрыт, а стык между ними —
/// нет: план строится по хешам, посчитанным одним кодом, а применяется совсем
/// другим, и разойтись они могут именно на настоящем диске (подкаталоги, регистр,
/// файлы, которых в публикации нет).
///
/// Живьём оба сценария пройдены 02.09.2026 на установленном клиенте 5.0.0
/// (467 файлов); здесь та же механика на публикации из 20 файлов.
/// </summary>
public sealed class ClientIntegrityRepairPipelineTests : IDisposable
{
    private const string Version = "5.0.0";

    /// <summary>
    /// Синтетическая публикация: 20 файлов с подкаталогами, не-ASCII именами и
    /// вложенностью — состав подобран так, чтобы план строился не только по плоскому
    /// корню (именно на подкаталогах разъезжаются относительные пути и разделители).
    /// </summary>
    private static readonly string[] PublicationFiles =
    [
        "Ven4Tools.exe",
        "Ven4Tools.dll",
        "Ven4Tools.runtimeconfig.json",
        "Newtonsoft.Json.dll",
        "System.Management.dll",
        "WebView2Loader.dll",
        "Assets/logo.png",
        "Assets/иконки/каталог.png",
        "Assets/иконки/система.png",
        "ru/Ven4Tools.resources.dll",
        "ru/справка.md",
        "Themes/тёмная.xaml",
        "Themes/светлая.xaml",
        "Scripts/debloat.ps1",
        "Scripts/активация.cmd",
        "Runtimes/win-x64/native.dll",
        "Runtimes/win-x64/native2.dll",
        "Docs/лицензия.txt",
        "Docs/история.md",
        "Docs/оглавление.md",
    ];

    private readonly TemporaryDirectory _area = new();

    /// <summary>Эталонная публикация — то, что в бою лежит на CDN отдельными файлами.</summary>
    private string PublicationPath => Path.Combine(_area.Path, "publication");

    /// <summary>Установка пользователя, которую проверяют и чинят.</summary>
    private string ClientPath => Path.Combine(_area.Path, "client");

    /// <summary>Временный каталог «скачанного» — аналог %TEMP%\Ven4Tools_Repair_*.</summary>
    private string WorkPath => Path.Combine(_area.Path, "work");

    /// <summary>Кэш состава установленной версии — уводим из %LOCALAPPDATA% в песочницу.</summary>
    private string StorePath => Path.Combine(_area.Path, "launcher-data", InstalledManifestStore.FileName);

    public void Dispose() => _area.Dispose();

    /// <summary>
    /// Паттерн А: испорчено 45% файлов (порог
    /// <see cref="ClientDeltaPlanner.MinimumUnchangedShare"/> пройден) плюс один
    /// лишний файл в папке. Починка обязана дойти до настоящей транзакции и вернуть
    /// папку в состояние, побайтово совпадающее с публикацией.
    /// </summary>
    [Fact]
    public async Task RepairableDamage_IsAppliedToRealFolderAndRestoresPublicationExactly()
    {
        CreatePublicationAndInstall();

        // 45%: девять файлов из двадцати, половина удалена, половина забита мусором —
        // так же, как в живом прогоне (210 повреждённых из 467).
        string[] damaged = PublicationFiles.Skip(1).Take(9).ToArray();
        DamageInstalledFiles(damaged);
        // Лишний файл: в публикации его нет, и починка обязана его удалить, иначе
        // «целостность подтверждена» означало бы «всё нужное на месте», а не «в папке
        // лежит ровно публикация».
        File.WriteAllText(Path.Combine(ClientPath, "чужой.dll"), "файл, которого в публикации нет");

        ClientFileManifest remote = await BuildPublicationManifestAsync();
        var executor = new LocalPublicationRepairExecutor(PublicationPath, WorkPath, StorePath);
        var checker = new ClientIntegrityChecker(new RealDiskEnvironment(remote), executor);

        ClientIntegrityReport report = await checker.CheckAsync(
            ClientPath, Version, Sources(), CancellationToken.None);

        Assert.Equal(ClientIntegrityStatus.RepairAvailable, report.Status);
        Assert.True(report.HasRepairableFindings);
        Assert.Equal(damaged.Length, report.Plan!.ToDownload.Count);
        Assert.Equal(PublicationFiles.Length - damaged.Length, report.Plan.Unchanged.Count);
        Assert.Equal(new[] { "чужой.dll" }, report.Plan.ToDelete);

        bool repaired = await checker.RepairAsync(report, ClientPath, CancellationToken.None);

        Assert.True(repaired);
        Assert.Equal(1, executor.Calls);

        // Независимая сверка: хеши считает сам тест, а не тот же построитель манифеста,
        // по которому строился план. Иначе ошибка в обходе папки «подтвердила» бы сама себя.
        AssertInstallIsByteIdenticalToPublication();

        // И тем же кодом, каким пользователь нажал бы «Проверить» второй раз.
        ClientIntegrityReport after = await checker.CheckAsync(
            ClientPath, Version, Sources(), CancellationToken.None);
        Assert.Equal(ClientIntegrityStatus.Healthy, after.Status);

        // Настоящий ClientDeltaInstaller после успешной транзакции пишет кэш состава —
        // его наличие подтверждает, что применение прошло целиком, а не до половины.
        Assert.True(File.Exists(StorePath));
    }

    /// <summary>
    /// Паттерн Б: испорчено 95% файлов. Пофайловая починка бессмысленна, и защита
    /// обязана сработать ДО установщика: качать почти всю публикацию по одному файлу
    /// медленнее и хрупче обычной полной переустановки. Проверяется не только вердикт,
    /// но и то, что папка осталась ровно в том виде, в каком была.
    /// </summary>
    [Fact]
    public async Task UnrepairableDamage_NeverReachesTheInstallerAndLeavesFolderAsIs()
    {
        CreatePublicationAndInstall();

        // 95%: всё, кроме самого исполняемого файла (в живом прогоне — 443 из 467).
        DamageInstalledFiles(PublicationFiles.Skip(1).ToArray());

        ClientFileManifest remote = await BuildPublicationManifestAsync();
        var executor = new LocalPublicationRepairExecutor(PublicationPath, WorkPath, StorePath);
        var checker = new ClientIntegrityChecker(new RealDiskEnvironment(remote), executor);

        ClientIntegrityReport report = await checker.CheckAsync(
            ClientPath, Version, Sources(), CancellationToken.None);

        Assert.Equal(ClientIntegrityStatus.FullReinstallRecommended, report.Status);
        Assert.True(report.Plan!.FullDownloadRecommended);
        Assert.False(report.HasRepairableFindings);
        Assert.Contains("1 из 20", report.Plan.Reason, StringComparison.Ordinal);
        Assert.Contains("дельта невыгодна", report.Plan.Reason, StringComparison.Ordinal);

        IReadOnlyDictionary<string, string> before = SnapshotDirectory(ClientPath);
        bool repaired = await checker.RepairAsync(report, ClientPath, CancellationToken.None);

        Assert.False(repaired);
        // Главное утверждение сценария: установщик не вызывался вовсе.
        Assert.Equal(0, executor.Calls);
        Assert.NotNull(report.RepairMessage);
        // Ни одного файла не тронуто — ни починенного, ни удалённого, ни служебного.
        Assert.Equal(before, SnapshotDirectory(ClientPath));
        Assert.False(File.Exists(StorePath));
    }

    private static ClientIntegritySources Sources() => new()
    {
        // Адреса до реального обращения не доходят (сеть подменена RealDiskEnvironment),
        // но должны быть заполнены: без FilesBaseUrl починка отказалась бы по своей
        // же проверке CanRepair, и тест мерил бы не то.
        ManifestUrl = "https://cdn.ven4tools.ru/client-files/5.0.0/client-manifest.json",
        ManifestSignatureUrl = "https://cdn.ven4tools.ru/client-files/5.0.0/client-manifest.json.sig",
        FilesBaseUrl = "https://cdn.ven4tools.ru/client-files/5.0.0/",
    };

    /// <summary>
    /// Раскладывает эталонную публикацию и её точную копию как установку пользователя.
    /// Содержимое файлов различается между собой — иначе одинаковые хеши скрыли бы
    /// путаницу путей в плане.
    /// </summary>
    private void CreatePublicationAndInstall()
    {
        foreach (string relative in PublicationFiles)
        {
            WriteFile(PublicationPath, relative, $"содержимое файла публикации {relative}");
            WriteFile(ClientPath, relative, $"содержимое файла публикации {relative}");
        }
    }

    /// <summary>
    /// Портит установленные файлы так же, как это происходит в жизни: часть исчезает
    /// (антивирус унёс файл в карантин), часть остаётся на месте с чужим содержимым
    /// (оборванная запись, подмена).
    /// </summary>
    private void DamageInstalledFiles(IReadOnlyList<string> relativePaths)
    {
        for (int i = 0; i < relativePaths.Count; i++)
        {
            string full = Path.Combine(
                ClientPath, relativePaths[i].Replace('/', Path.DirectorySeparatorChar));
            if (i % 2 == 0)
            {
                File.Delete(full);
            }
            else
            {
                File.WriteAllText(full, $"повреждённое содержимое {i}");
            }
        }
    }

    private Task<ClientFileManifest> BuildPublicationManifestAsync() =>
        ClientManifestBuilder.BuildFromDirectoryAsync(PublicationPath, Version, CancellationToken.None);

    private static void WriteFile(string root, string relativePath, string content)
    {
        string full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>Относительный путь → SHA256 каждого файла каталога.</summary>
    private static IReadOnlyDictionary<string, string> SnapshotDirectory(string root)
    {
        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            using var stream = File.OpenRead(file);
            snapshot[Path.GetRelativePath(root, file).Replace('\\', '/')] =
                Convert.ToHexString(SHA256.HashData(stream));
        }
        return snapshot;
    }

    private void AssertInstallIsByteIdenticalToPublication()
    {
        Assert.Equal(SnapshotDirectory(PublicationPath), SnapshotDirectory(ClientPath));

        // Служебных заготовок и резервных копий транзакции после успеха остаться не должно.
        Assert.DoesNotContain(
            Directory.EnumerateFiles(ClientPath, "*", SearchOption.AllDirectories),
            file => TransactionalDirectoryInstaller.IsTransientArtifactName(Path.GetFileName(file)));
    }

    /// <summary>
    /// Настоящий диск вместо подмен: состав папки считается реальным построителем
    /// манифеста, наличие exe — реальной проверкой файла. Подменён только поход в
    /// сеть за эталоном (в бою — подписанный client-manifest.json с CDN) и ACL:
    /// у каталога в %TEMP% права заведомо «ослаблены», и к сценарию это отношения
    /// не имеет.
    /// </summary>
    private sealed class RealDiskEnvironment : IClientIntegrityEnvironment
    {
        private readonly ClientFileManifest _remote;

        public RealDiskEnvironment(ClientFileManifest remote) => _remote = remote;

        public bool ClientExecutableExists(string clientPath) =>
            File.Exists(Path.Combine(clientPath, LauncherPaths.ClientExeName));

        public Task<ClientFileManifest> BuildLocalManifestAsync(
            string clientPath, string versionLabel, CancellationToken cancellationToken) =>
            ClientManifestBuilder.BuildFromDirectoryAsync(clientPath, versionLabel, cancellationToken);

        public Task<ClientFileManifest?> FetchRemoteManifestAsync(
            string manifestUrl, string signatureUrl, CancellationToken cancellationToken) =>
            Task.FromResult<ClientFileManifest?>(_remote);

        public bool IsAclCompromised(string clientPath) => false;
    }

    /// <summary>
    /// То же, что делает MainWindow.Repair.cs, но файлы берутся из локальной папки
    /// публикации вместо HTTP. Применение — настоящее: тот же
    /// <see cref="ClientDeltaInstaller"/> и та же транзакция
    /// <see cref="TransactionalDirectoryInstaller.InstallPartial"/>, что и в бою.
    /// </summary>
    private sealed class LocalPublicationRepairExecutor : IClientRepairExecutor
    {
        private readonly string _publicationPath;
        private readonly string _workPath;
        private readonly string _storePath;

        public LocalPublicationRepairExecutor(string publicationPath, string workPath, string storePath)
        {
            _publicationPath = publicationPath;
            _workPath = workPath;
            _storePath = storePath;
        }

        /// <summary>Сколько раз починка реально дошла до установщика.</summary>
        public int Calls { get; private set; }

        public Task<bool> ApplyAsync(
            ClientFileManifest remoteManifest,
            ClientDeltaPlan plan,
            ClientIntegritySources sources,
            string clientPath,
            CancellationToken cancellationToken)
        {
            Calls++;
            Directory.CreateDirectory(_workPath);

            // Аналог DownloadChangedFilesAsync: плоские имена во временном каталоге,
            // раскладку по подкаталогам делает сама транзакция по RelativePath.
            var updates = new List<PartialFileUpdate>();
            for (int i = 0; i < plan.ToDownload.Count; i++)
            {
                string relative = plan.ToDownload[i].Path!;
                string source = Path.Combine(
                    _publicationPath, relative.Replace('/', Path.DirectorySeparatorChar));
                string staged = Path.Combine(_workPath, $"{i:D5}.bin");
                File.Copy(source, staged, overwrite: true);
                updates.Add(new PartialFileUpdate(relative, staged));
            }

            using var downloaded = new DeltaDownloadSet(updates, []);
            var installer = new ClientDeltaInstaller(
                new FallbackDownloader(),
                new TransactionalDirectoryInstaller(),
                new InstalledManifestStore(_storePath));
            installer.Apply(remoteManifest, plan, downloaded, clientPath, log: null, cancellationToken);

            return Task.FromResult(true);
        }
    }
}
