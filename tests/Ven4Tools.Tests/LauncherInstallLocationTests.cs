using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Папка установки клиента (лаунчер 3.12): классификация пути (OneDrive, «Контролируемый
/// доступ к папкам»), проверки и журнал переноса, ярлыки.
/// </summary>
public sealed class LauncherInstallLocationTests
{
    // --- InstallLocationRisk ---

    [Theory]
    [InlineData(@"C:\Users\u\OneDrive\Документы\Ven4Tools", true)]
    [InlineData(@"C:\Users\u\OneDrive", true)]
    [InlineData(@"c:\users\U\onedrive\x", true)]
    [InlineData(@"C:\Users\u\OneDriveX\Ven4Tools", false)]
    [InlineData(@"C:\Users\u\AppData\Local\Programs\Ven4Tools", false)]
    public void IsUnderAnyRoot_RespectsFolderBoundary(string path, bool expected)
    {
        Assert.Equal(expected, InstallLocationRisk.IsUnderAnyRoot(path, new[] { @"C:\Users\u\OneDrive", null, "" }));
    }

    [Fact]
    public void IsCloudPlaceholder_DetectsRecallAttributes()
    {
        Assert.True(InstallLocationRisk.IsCloudPlaceholder(InstallLocationRisk.RecallOnDataAccess | 0x10));
        Assert.True(InstallLocationRisk.IsCloudPlaceholder(InstallLocationRisk.RecallOnOpen));
        Assert.False(InstallLocationRisk.IsCloudPlaceholder((int)(FileAttributes.Directory | FileAttributes.ReparsePoint)));
    }

    private static readonly string[] DefaultProtected = { @"C:\Users\u\Documents", @"C:\Users\u\Desktop" };

    [Fact]
    public void Cfa_BlockModeAndProtectedFolder_IsRisky()
    {
        var status = new ControlledFolderAccessStatus(true, Array.Empty<string>(), Array.Empty<string>());
        Assert.True(InstallLocationRisk.IsProtectedByCfa(@"C:\Users\u\Documents\Ven4Tools_Client", status, DefaultProtected, @"C:\L\launcher.exe"));
        Assert.False(InstallLocationRisk.IsProtectedByCfa(@"C:\Users\u\AppData\Local\Programs\Ven4Tools", status, DefaultProtected, @"C:\L\launcher.exe"));
    }

    [Fact]
    public void Cfa_NoData_IsNeverRisky()
    {
        // Нет данных о режиме — не пугаем, даже для «Документов».
        Assert.False(InstallLocationRisk.IsProtectedByCfa(
            @"C:\Users\u\Documents\x", ControlledFolderAccessStatus.Unknown, DefaultProtected, null));
        var off = new ControlledFolderAccessStatus(false, Array.Empty<string>(), Array.Empty<string>());
        Assert.False(InstallLocationRisk.IsProtectedByCfa(@"C:\Users\u\Documents\x", off, DefaultProtected, null));
    }

    [Fact]
    public void Cfa_CustomFolderAndAllowedApp()
    {
        var custom = new ControlledFolderAccessStatus(true, new[] { @"D:\Games" }, Array.Empty<string>());
        Assert.True(InstallLocationRisk.IsProtectedByCfa(@"D:\Games\Ven4Tools", custom, DefaultProtected, null));

        // Лаунчер в списке разрешённых программ — запись ему не блокируется.
        var allowed = new ControlledFolderAccessStatus(true, Array.Empty<string>(), new[] { @"C:\L\Launcher.EXE" });
        Assert.False(InstallLocationRisk.IsProtectedByCfa(@"C:\Users\u\Documents\x", allowed, DefaultProtected, @"C:\L\launcher.exe"));
    }

    [Fact]
    public void DefaultInstallRoot_IsUnderLocalAppDataPrograms()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ven4Tools");
        Assert.Equal(expected, LauncherPaths.DefaultInstallRoot);
    }

    // --- Перенос клиента ---

    [Fact]
    public void ValidateTarget_RefusesSameNestedAndNonEmpty()
    {
        using var area = new TemporaryDirectory();
        string data = Path.Combine(area.Path, "data");
        string source = Path.Combine(area.Path, "old", "Ven4Tools_Client");

        Assert.NotNull(ClientFolderMover.ValidateTarget(source, source + "\\", data));
        Assert.NotNull(ClientFolderMover.ValidateTarget(source, Path.Combine(source, "inner", "Ven4Tools_Client"), data));
        Assert.NotNull(ClientFolderMover.ValidateTarget(source, Path.Combine(data, "Ven4Tools_Client"), data));

        string busy = Path.Combine(area.Path, "busy", "Ven4Tools_Client");
        Directory.CreateDirectory(busy);
        File.WriteAllText(Path.Combine(busy, "чужой.txt"), "x");
        Assert.NotNull(ClientFolderMover.ValidateTarget(source, busy, data));

        string fresh = Path.Combine(area.Path, "new", "Ven4Tools_Client");
        Assert.Null(ClientFolderMover.ValidateTarget(source, fresh, data));

        // Пустая существующая папка допустима.
        Directory.CreateDirectory(fresh);
        Assert.Null(ClientFolderMover.ValidateTarget(source, fresh, data));
    }

    [Fact]
    public void ManifestCompare_FindsExtraMissingAndChanged()
    {
        var expected = new Dictionary<string, ClientFolderManifest.Entry>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ven4Tools.exe"] = new(10, "AA"),
            ["lib/a.dll"] = new(5, "BB"),
        };

        Assert.Null(ClientFolderManifest.Compare(expected, new Dictionary<string, ClientFolderManifest.Entry>(expected)));

        var missing = new Dictionary<string, ClientFolderManifest.Entry> { ["Ven4Tools.exe"] = new(10, "aa") };
        Assert.Contains("lib/a.dll", ClientFolderManifest.Compare(expected, missing));

        var changed = new Dictionary<string, ClientFolderManifest.Entry>(expected) { ["lib/a.dll"] = new(5, "CC") };
        Assert.Contains("отличается", ClientFolderManifest.Compare(expected, changed));

        var extra = new Dictionary<string, ClientFolderManifest.Entry>(expected) { ["лишний.txt"] = new(1, "DD") };
        Assert.Contains("лишний", ClientFolderManifest.Compare(expected, extra));
    }

    [Fact]
    public async Task CopyTree_ProducesCopyMatchingManifest()
    {
        using var area = new TemporaryDirectory();
        string source = Path.Combine(area.Path, "src");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "Ven4Tools.exe"), "exe");
        File.WriteAllText(Path.Combine(source, "sub", "a.dll"), new string('z', 300_000));

        var manifest = await ClientFolderManifest.CaptureAsync(source, CancellationToken.None);
        string staging = Path.Combine(area.Path, "dst");
        await ClientFolderMover.CopyTreeAsync(source, staging, null, CancellationToken.None);

        Assert.Equal(2, manifest.FileCount);
        Assert.Null(await manifest.FindDifferenceAsync(staging, CancellationToken.None));

        File.WriteAllText(Path.Combine(staging, "sub", "a.dll"), "испорчен");
        Assert.NotNull(await manifest.FindDifferenceAsync(staging, CancellationToken.None));
    }

    [Theory]
    [InlineData(@"D:\New\Ven4Tools_Client", nameof(ClientMoveRecoveryAction.RemoveOldCopy))]
    [InlineData(@"C:\Users\u\Documents\Ven4Tools_Client", nameof(ClientMoveRecoveryAction.RemoveNewCopy))]
    [InlineData(@"E:\Other\Ven4Tools_Client", nameof(ClientMoveRecoveryAction.None))]
    public void MoveJournal_DecidesByPointer(string pointer, string expected)
    {
        var journal = new ClientMoveJournal
        {
            OldClientPath = @"C:\Users\u\Documents\Ven4Tools_Client",
            NewClientPath = @"D:\New\Ven4Tools_Client",
            StagingPath = @"D:\New\.Ven4Tools_Client.staging-1",
        };
        Assert.Equal(expected, ClientMoveJournal.Decide(journal, pointer).ToString());
    }

    [Fact]
    public void MoveJournal_RoundTripsThroughFile()
    {
        using var area = new TemporaryDirectory();
        string path = Path.Combine(area.Path, "client-move.json");
        new ClientMoveJournal { OldClientPath = @"C:\a", NewClientPath = @"D:\b", StagingPath = @"D:\.b" }.Save(path);

        var loaded = ClientMoveJournal.Load(path);
        Assert.Equal(@"D:\b", loaded!.NewClientPath);

        File.WriteAllText(path, "{ битый");
        Assert.Null(ClientMoveJournal.Load(path));
    }

    // --- Ярлыки ---

    [Theory]
    [InlineData(@"C:\Users\u\Documents\Ven4Tools_Client\Ven4Tools.exe", @"D:\New\Ven4Tools_Client\Ven4Tools.exe")]
    [InlineData(@"C:\Users\u\Documents\Ven4Tools_Client", @"D:\New\Ven4Tools_Client")]
    [InlineData(@"c:\users\u\documents\ven4tools_client\sub\x.ico", @"D:\New\Ven4Tools_Client\sub\x.ico")]
    [InlineData(@"C:\Users\u\Documents\Ven4Tools_Client2\Ven4Tools.exe", null)]
    [InlineData(@"C:\Windows\explorer.exe", null)]
    [InlineData("", null)]
    public void MapPath_OnlyInsideOldClientFolder(string path, string? expected)
    {
        Assert.Equal(expected, ClientShortcutRetargeter.MapPath(
            path, @"C:\Users\u\Documents\Ven4Tools_Client", @"D:\New\Ven4Tools_Client"));
    }

    [Fact]
    public void MapIconLocation_KeepsIconIndex()
    {
        Assert.Equal(@"D:\New\Ven4Tools_Client\Ven4Tools.exe,0", ClientShortcutRetargeter.MapIconLocation(
            @"C:\Old\Ven4Tools_Client\Ven4Tools.exe,0", @"C:\Old\Ven4Tools_Client", @"D:\New\Ven4Tools_Client"));
        Assert.Null(ClientShortcutRetargeter.MapIconLocation(
            @"C:\Windows\system32\shell32.dll,3", @"C:\Old\Ven4Tools_Client", @"D:\New\Ven4Tools_Client"));
    }
}
