using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Лаунчер 3.12.1: итог самообновления от установщика, папка клиента с посторонними
/// файлами и показ версии клиента.
/// </summary>
public sealed class LauncherInstallSafetyTests
{
    // ── Итог самообновления (LauncherUpdateResult) ──

    [Theory]
    [InlineData("0;3.12.1", 0, "3.12.1")]
    [InlineData("5;3.12.1", 5, "3.12.1")]
    [InlineData(" 3 ; 3.13.0 ", 3, "3.13.0")]
    public void UpdateResult_Parses(string raw, int code, string version)
    {
        var result = LauncherUpdateResult.Parse(raw);
        Assert.NotNull(result);
        Assert.Equal(code, result!.Value.Code);
        Assert.Equal(version, result.Value.Version);
        Assert.Equal(code == 0, result.Value.Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("мусор")]
    [InlineData("x;3.12.1")]
    [InlineData("5;")]
    [InlineData("5;0123456789012345678901234567890123456789")]
    public void UpdateResult_RejectsGarbage(string? raw) =>
        Assert.Null(LauncherUpdateResult.Parse(raw));

    [Theory]
    [InlineData(5, "занят")]
    [InlineData(4, "версия")]
    [InlineData(3, "записать")]
    [InlineData(9, "кодом 9")]
    public void UpdateResult_DescribesFailure(int code, string fragment) =>
        Assert.Contains(fragment, new LauncherUpdateResult(code, "3.12.1").DescribeFailure());

    // ── Посторонние файлы в папке клиента (ClientFolderInspector) ──

    private const string DepsJson = """
        {
          "targets": {
            ".NETCoreApp,Version=v10.0/win-x64": {
              "Ven4Tools/6.5.1": { "runtime": { "Ven4Tools.dll": {} } },
              "Newtonsoft.Json/13.0.3": { "runtime": { "lib/net6.0/Newtonsoft.Json.dll": {} } },
              "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.0": {
                "runtime": { "System.Private.CoreLib.dll": {} },
                "native": { "coreclr.dll": {}, "hostfxr.dll": {} }
              }
            }
          }
        }
        """;

    private static void CreateCleanClient(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Ven4Tools.deps.json"), DepsJson);
        foreach (string name in new[] { "Ven4Tools.exe", "Ven4Tools.dll", "Ven4Tools.runtimeconfig.json",
                     "Newtonsoft.Json.dll", "System.Private.CoreLib.dll", "coreclr.dll", "hostfxr.dll", "createdump.exe" })
            File.WriteAllText(Path.Combine(dir, name), "x");
        Directory.CreateDirectory(Path.Combine(dir, "ru"));
        Directory.CreateDirectory(Path.Combine(dir, "zh-Hans"));
        Directory.CreateDirectory(Path.Combine(dir, "Data"));
        File.WriteAllText(Path.Combine(dir, "Data", "master.json"), "{}");
    }

    [Fact]
    public void Inspector_CleanClientFolder_HasNoForeignEntries()
    {
        using var area = new TemporaryDirectory();
        string client = Path.Combine(area.Path, "Ven4Tools_Client");
        CreateCleanClient(client);
        // Остаток прерванного блочного обновления — свой файл.
        File.WriteAllText(Path.Combine(client, "coreclr.dll.old-0123456789abcdef0123456789abcdef"), "x");

        Assert.Empty(ClientFolderInspector.FindForeignEntries(client));
    }

    [Fact]
    public void Inspector_OtherProgramsNextToClient_AreForeign()
    {
        using var area = new TemporaryDirectory();
        string tools = Path.Combine(area.Path, "Tools");
        CreateCleanClient(tools);
        File.WriteAllText(Path.Combine(tools, "putty.exe"), "x");
        File.WriteAllText(Path.Combine(tools, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(tools, "SomeOther.dll"), "x");
        Directory.CreateDirectory(Path.Combine(tools, "Far Manager"));

        var foreign = ClientFolderInspector.FindForeignEntries(tools, limit: int.MaxValue);

        Assert.Contains("putty.exe", foreign);
        Assert.Contains("notes.txt", foreign);
        Assert.Contains("SomeOther.dll", foreign);
        Assert.Contains("Far Manager" + Path.DirectorySeparatorChar, foreign);
        Assert.Equal(4, foreign.Count);
    }

    [Fact]
    public void Inspector_WithoutDepsJson_ForeignExeIsStillForeign()
    {
        var inspector = ClientFolderInspector.FromListedFiles(null);
        Assert.True(inspector.IsOwnFileName("Ven4Tools.exe"));
        Assert.True(inspector.IsOwnFileName("System.Text.Json.dll"));
        Assert.False(inspector.IsOwnFileName("putty.exe"));
        Assert.False(inspector.IsOwnFileName("readme.txt"));
    }

    [Theory]
    [InlineData("ru", true)]
    [InlineData("pt-BR", true)]
    [InlineData("zh-Hant", true)]
    [InlineData("Data", true)]
    [InlineData("lang", true)]
    [InlineData("Games", false)]
    [InlineData("backup", false)]
    public void Inspector_DirectoryNames(string name, bool own) =>
        Assert.Equal(own, ClientFolderInspector.IsOwnDirectoryName(name));

    [Fact]
    public void Inspector_ParsesDepsJson()
    {
        var listed = ClientFolderInspector.ParseListedFiles(DepsJson);
        Assert.NotNull(listed);
        Assert.Contains("Newtonsoft.Json.dll", listed!);
        Assert.Contains("hostfxr.dll", listed);
        Assert.Null(ClientFolderInspector.ParseListedFiles("{}"));
    }

    [Fact]
    public void DeleteOwnEntries_LeavesForeignFiles()
    {
        using var area = new TemporaryDirectory();
        string tools = Path.Combine(area.Path, "Tools");
        CreateCleanClient(tools);
        File.WriteAllText(Path.Combine(tools, "putty.exe"), "keep");
        Directory.CreateDirectory(Path.Combine(tools, "Far Manager"));

        ClientFolderInspector.DeleteOwnEntries(tools);

        Assert.True(File.Exists(Path.Combine(tools, "putty.exe")));
        Assert.True(Directory.Exists(Path.Combine(tools, "Far Manager")));
        Assert.False(File.Exists(Path.Combine(tools, "Ven4Tools.exe")));
        Assert.False(File.Exists(Path.Combine(tools, "Ven4Tools.deps.json")));
        Assert.False(Directory.Exists(Path.Combine(tools, "ru")));
        Assert.False(Directory.Exists(Path.Combine(tools, "Data")));
    }

    // ── Установка поверх папки с посторонними файлами ──

    [Fact]
    public void Install_KeepReplacedAs_PreservesPreviousContent()
    {
        using var area = new TemporaryDirectory();
        string target = Path.Combine(area.Path, "Tools");
        string staging = Path.Combine(area.Path, ".Tools.staging-x");
        string kept = Path.Combine(area.Path, "Tools (до обновления)");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(target, "putty.exe"), "keep");
        File.WriteAllText(Path.Combine(staging, "Ven4Tools.exe"), "new");

        new TransactionalDirectoryInstaller().Install(staging, target, CancellationToken.None, kept);

        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "Ven4Tools.exe")));
        Assert.False(File.Exists(Path.Combine(target, "putty.exe")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(kept, "putty.exe")));
        Assert.Empty(Directory.GetDirectories(area.Path, "Tools.backup-*"));
    }

    // ── Показ версии ──

    [Theory]
    [InlineData("6.5.0.0", "6.5.0")]
    [InlineData("6.5.0", "6.5.0")]
    [InlineData("6.5.0.7", "6.5.0.7")]
    [InlineData("3.12.1+abc", "3.12.1+abc")]
    [InlineData("unknown", "unknown")]
    [InlineData("", "")]
    public void Version_ToDisplay(string input, string expected) =>
        Assert.Equal(expected, VersionComparer.ToDisplay(input));
}
