using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Логика принятия решений функции «Проверить и восстановить клиент»: какой вердикт
/// строится из каких входов. Диск, сеть и ACL подменены — проверяется именно выбор
/// вердикта, а сравнение манифестов покрыто отдельно (ClientDeltaPlannerTests).
///
/// Главное, что здесь защищается, — разница между «клиент повреждён» и «нам не с чем
/// сравнить». Спутать их означает сказать пользователю с исправной установкой, что
/// она сломана, и наоборот.
/// </summary>
public sealed class ClientIntegrityCheckerTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string ClientPath = @"C:\Ven4Tools\Ven4Tools_Client";

    private static ClientFileManifest Manifest(string version, params (string Path, string Hash)[] files) =>
        new()
        {
            Version = version,
            Files = files
                .Select(f => new ClientManifestFileEntry { Path = f.Path, Sha256 = f.Hash, Size = 100 })
                .ToList(),
        };

    // Публикация из четырёх файлов: одно расхождение — это 75% совпадений, что выше
    // порога ClientDeltaPlanner.MinimumUnchangedShare, три расхождения — 25%, ниже.
    private static (string Path, string Hash)[] Publication(params string[] hashes) =>
        hashes.Select((h, i) => ($"file{i}.dll", h)).ToArray();

    private static ClientIntegritySources FullSources() => new()
    {
        ManifestUrl = "https://cdn.ven4tools.ru/client-files/5.0.0/client-manifest.json",
        ManifestSignatureUrl = "https://cdn.ven4tools.ru/client-files/5.0.0/client-manifest.json.sig",
        FilesBaseUrl = "https://cdn.ven4tools.ru/client-files/5.0.0/",
    };

    private sealed class FakeEnvironment : IClientIntegrityEnvironment
    {
        public bool ExeExists { get; set; } = true;
        public ClientFileManifest? Local { get; set; }
        public Exception? LocalError { get; set; }
        public ClientFileManifest? Remote { get; set; }
        public Exception? RemoteError { get; set; }
        public bool Acl { get; set; }

        public int FetchCalls { get; private set; }
        public int BuildCalls { get; private set; }

        public bool ClientExecutableExists(string clientPath) => ExeExists;

        public Task<ClientFileManifest> BuildLocalManifestAsync(
            string clientPath, string versionLabel, CancellationToken cancellationToken)
        {
            BuildCalls++;
            if (LocalError != null) throw LocalError;
            return Task.FromResult(Local!);
        }

        public Task<ClientFileManifest?> FetchRemoteManifestAsync(
            string manifestUrl, string signatureUrl, CancellationToken cancellationToken)
        {
            FetchCalls++;
            if (RemoteError != null) throw RemoteError;
            return Task.FromResult(Remote);
        }

        public bool IsAclCompromised(string clientPath) => Acl;
    }

    private sealed class FakeRepairExecutor : IClientRepairExecutor
    {
        public bool Result { get; set; } = true;
        public int Calls { get; private set; }
        public ClientDeltaPlan? ReceivedPlan { get; private set; }

        public Task<bool> ApplyAsync(
            ClientFileManifest remoteManifest,
            ClientDeltaPlan plan,
            ClientIntegritySources sources,
            string clientPath,
            CancellationToken cancellationToken)
        {
            Calls++;
            ReceivedPlan = plan;
            return Task.FromResult(Result);
        }
    }

    private static ClientIntegrityChecker Checker(
        FakeEnvironment environment, IClientRepairExecutor? executor = null) =>
        new(environment, executor);

    [Fact]
    public async Task Check_ClientNotInstalled_ReportsWithoutTouchingNetwork()
    {
        var environment = new FakeEnvironment { ExeExists = false };

        var report = await Checker(environment)
            .CheckAsync(ClientPath, "5.0.0", FullSources(), CancellationToken.None);

        Assert.Equal(ClientIntegrityStatus.NotInstalled, report.Status);
        Assert.False(report.IsClientInstalled);
        // Ни хеширования папки, ни запроса к CDN: проверять нечего.
        Assert.Equal(0, environment.BuildCalls);
        Assert.Equal(0, environment.FetchCalls);
    }

    [Fact]
    public async Task Check_AllFilesMatch_ReportsHealthy()
    {
        var files = Publication(HashA, HashA, HashA, HashA);
        var environment = new FakeEnvironment
        {
            Local = Manifest("5.0.0", files),
            Remote = Manifest("5.0.0", files),
        };

        var report = await Checker(environment)
            .CheckAsync(ClientPath, "5.0.0", FullSources(), CancellationToken.None);

        Assert.Equal(ClientIntegrityStatus.Healthy, report.Status);
        Assert.True(report.ManifestAvailable);
        Assert.False(report.HasRepairableFindings);
        Assert.False(report.AclCompromised);
    }

    [Fact]
    public async Task Check_DamagedFile_ReportsRepairable()
    {
        var environment = new FakeEnvironment
        {
            // Второй файл на диске отличается — так выглядит выкушенная антивирусом
            // или недописанная библиотека.
            Local = Manifest("5.0.0", Publication(HashA, HashB, HashA, HashA)),
            Remote = Manifest("5.0.0", Publication(HashA, HashA, HashA, HashA)),
        };

        var report = await Checker(environment)
            .CheckAsync(ClientPath, "5.0.0", FullSources(), CancellationToken.None);

        Assert.Equal(ClientIntegrityStatus.RepairAvailable, report.Status);
        Assert.True(report.HasRepairableFindings);
        Assert.NotNull(report.Plan);
        Assert.Equal(new[] { "file1.dll" }, report.Plan!.ToDownload.Select(e => e.Path));
    }

    [Fact]
    public async Task Check_ExtraFileOnDisk_IsReportedForDeletion()
    {
        var environment = new FakeEnvironment
        {
            Local = Manifest("5.0.0",
                Publication(HashA, HashA, HashA, HashA).Append(("intruder.dll", HashB)).ToArray()),
            Remote = Manifest("5.0.0", Publication(HashA, HashA, HashA, HashA)),
        };

        var report = await Checker(environment)
            .CheckAsync(ClientPath, "5.0.0", FullSources(), CancellationToken.None);

        Assert.Equal(ClientIntegrityStatus.RepairAvailable, report.Status);
        Assert.Equal(new[] { "intruder.dll" }, report.Plan!.ToDelete);
    }

    [Fact]
    public async Task Check_TooManyDifferences_RecommendsFullReinstall()
    {
        var environment = new FakeEnvironment
        {
            Local = Manifest("5.0.0", Publication(HashB, HashB, HashB, HashA)),
            Remote = Manifest("5.0.0", Publication(HashA, HashA, HashA, HashA)),
        };

        var report = await Checker(environment)
            .CheckAsync(ClientPath, "5.0.0", FullSources(), CancellationToken.None);

        Assert.Equal(ClientIntegrityStatus.FullReinstallRecommended, report.Status);
        // Починка такого не предлагается — только текст «переустановите обычным путём».
        Assert.False(report.HasRepairableFindings);
    }

    [Fact]
    public async Task Check_NoManifestPublishedForVersion_IsNotAVerdictAboutTheClient()
    {
        var environment = new FakeEnvironment
        {
            Local = Manifest("4.9.0", Publication(HashA, HashA)),
        };

        // Релиз выпущен до появления файловых манифестов — адресов попросту нет.
        var report = await Checker(environment)
            .CheckAsync(ClientPath, "4.9.0", new ClientIntegritySources(), CancellationToken.None);

        Assert.Equal(ClientIntegrityStatus.ManifestUnavailable, report.Status);
        Assert.True(report.IsClientInstalled);
        Assert.False(report.ManifestAvailable);
        Assert.Null(report.Plan);
        Assert.Equal(0, environment.FetchCalls);
    }

    [Fact]
    public async Task Check_ManifestFetchFails_ReportsUnavailableNotDamage()
    {
        var environment = new FakeEnvironment
        {
            Local = Manifest("5.0.0", Publication(HashA, HashA)),
            Remote = null, // FetchAsync fail-closed: сеть недоступна либо подпись не сошлась
        };

        var report = await Checker(environment)
            .CheckAsync(ClientPath, "5.0.0", FullSources(), CancellationToken.None);

        Assert.Equal(ClientIntegrityStatus.ManifestUnavailable, report.Status);
        Assert.Null(report.Plan);
    }

    [Fact]
    public async Task Check_ManifestDescribesAnotherVersion_IsRefused()
    {
        // Ключевая защита: на CDN лежит манифест ПОСЛЕДНЕЙ версии. Сверять с ним
        // установленную 4.9.0 — значит объявить повреждённым каждый файл, который
        // просто изменился между релизами, то есть весь клиент у неспешного пользователя.
        var environment = new FakeEnvironment
        {
            Local = Manifest("4.9.0", Publication(HashB, HashB, HashB, HashB)),
            Remote = Manifest("5.0.0", Publication(HashA, HashA, HashA, HashA)),
        };

        var report = await Checker(environment)
            .CheckAsync(ClientPath, "4.9.0", FullSources(), CancellationToken.None);

        Assert.Equal(ClientIntegrityStatus.ManifestUnavailable, report.Status);
        Assert.Null(report.Plan);
        Assert.Contains("5.0.0", report.Summary);
    }

    [Fact]
    public async Task Check_FourPartFileVersion_MatchesThreePartManifestVersion()
    {
        // Установленная версия читается из метаданных exe («5.0.0.0»), а манифест
        // подписан под «5.0.0». Строковое сравнение отвергло бы совпадающие версии
        // и сделало бы проверку бесполезной для всех.
        var files = Publication(HashA, HashA);
        var environment = new FakeEnvironment
        {
            Local = Manifest("5.0.0.0", files),
            Remote = Manifest("5.0.0", files),
        };

        var report = await Checker(environment)
            .CheckAsync(ClientPath, "5.0.0.0", FullSources(), CancellationToken.None);

        Assert.Equal(ClientIntegrityStatus.Healthy, report.Status);
    }

    [Fact]
    public async Task Check_WeakenedAcl_IsReportedEvenWhenManifestUnavailable()
    {
        var environment = new FakeEnvironment
        {
            Local = Manifest("5.0.0", Publication(HashA, HashA)),
            Remote = null,
            Acl = true,
        };

        var report = await Checker(environment)
            .CheckAsync(ClientPath, "5.0.0", FullSources(), CancellationToken.None);

        Assert.Equal(ClientIntegrityStatus.ManifestUnavailable, report.Status);
        Assert.True(report.AclCompromised);
    }

    [Fact]
    public async Task Check_UnreadableClientFolder_ReportsCheckFailed()
    {
        var environment = new FakeEnvironment
        {
            LocalError = new IOException("файл занят другим процессом"),
        };

        var report = await Checker(environment)
            .CheckAsync(ClientPath, "5.0.0", FullSources(), CancellationToken.None);

        Assert.Equal(ClientIntegrityStatus.CheckFailed, report.Status);
        Assert.Null(report.Plan);
        // До сети дело не дошло — сравнивать всё равно было бы не с чем.
        Assert.Equal(0, environment.FetchCalls);
    }

    [Fact]
    public async Task Repair_AppliesPlanWhenFindingsAreRepairable()
    {
        var environment = new FakeEnvironment
        {
            Local = Manifest("5.0.0", Publication(HashA, HashB, HashA, HashA)),
            Remote = Manifest("5.0.0", Publication(HashA, HashA, HashA, HashA)),
        };
        var executor = new FakeRepairExecutor();
        var checker = Checker(environment, executor);

        var report = await checker.CheckAsync(ClientPath, "5.0.0", FullSources(), CancellationToken.None);
        bool repaired = await checker.RepairAsync(report, ClientPath, CancellationToken.None);

        Assert.True(repaired);
        Assert.Equal(1, executor.Calls);
        Assert.Same(report.Plan, executor.ReceivedPlan);
    }

    [Fact]
    public async Task Repair_FullReinstallCase_DoesNothingItself()
    {
        var environment = new FakeEnvironment
        {
            Local = Manifest("5.0.0", Publication(HashB, HashB, HashB, HashA)),
            Remote = Manifest("5.0.0", Publication(HashA, HashA, HashA, HashA)),
        };
        var executor = new FakeRepairExecutor();
        var checker = Checker(environment, executor);

        var report = await checker.CheckAsync(ClientPath, "5.0.0", FullSources(), CancellationToken.None);
        bool repaired = await checker.RepairAsync(report, ClientPath, CancellationToken.None);

        Assert.False(repaired);
        // Никакой самодеятельной полной переустановки — у пользователя для этого
        // есть обычный путь обновления.
        Assert.Equal(0, executor.Calls);
        Assert.NotNull(report.RepairMessage);
    }

    [Fact]
    public async Task Repair_HealthyClient_HasNothingToDo()
    {
        var files = Publication(HashA, HashA);
        var environment = new FakeEnvironment
        {
            Local = Manifest("5.0.0", files),
            Remote = Manifest("5.0.0", files),
        };
        var executor = new FakeRepairExecutor();
        var checker = Checker(environment, executor);

        var report = await checker.CheckAsync(ClientPath, "5.0.0", FullSources(), CancellationToken.None);
        bool repaired = await checker.RepairAsync(report, ClientPath, CancellationToken.None);

        Assert.False(repaired);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task Repair_WithoutFilesBaseUrl_IsRefused()
    {
        var environment = new FakeEnvironment
        {
            Local = Manifest("5.0.0", Publication(HashA, HashB, HashA, HashA)),
            Remote = Manifest("5.0.0", Publication(HashA, HashA, HashA, HashA)),
        };
        var executor = new FakeRepairExecutor();
        var checker = Checker(environment, executor);

        // Манифест опубликован, а отдельные файлы версии — нет: проверить можно,
        // починить нечем.
        var sources = new ClientIntegritySources
        {
            ManifestUrl = "https://cdn.ven4tools.ru/client-files/5.0.0/client-manifest.json",
            ManifestSignatureUrl = "https://cdn.ven4tools.ru/client-files/5.0.0/client-manifest.json.sig",
        };

        var report = await checker.CheckAsync(ClientPath, "5.0.0", sources, CancellationToken.None);
        Assert.Equal(ClientIntegrityStatus.RepairAvailable, report.Status);

        bool repaired = await checker.RepairAsync(report, ClientPath, CancellationToken.None);

        Assert.False(repaired);
        Assert.Equal(0, executor.Calls);
        Assert.NotNull(report.RepairMessage);
    }

    [Fact]
    public async Task Repair_FailedApply_ExplainsItselfThroughTheReport()
    {
        var environment = new FakeEnvironment
        {
            Local = Manifest("5.0.0", Publication(HashA, HashB, HashA, HashA)),
            Remote = Manifest("5.0.0", Publication(HashA, HashA, HashA, HashA)),
        };
        var executor = new FakeRepairExecutor { Result = false };
        var checker = Checker(environment, executor);

        var report = await checker.CheckAsync(ClientPath, "5.0.0", FullSources(), CancellationToken.None);
        bool repaired = await checker.RepairAsync(report, ClientPath, CancellationToken.None);

        Assert.False(repaired);
        Assert.Equal(1, executor.Calls);
        Assert.NotNull(report.RepairMessage);
    }
}
