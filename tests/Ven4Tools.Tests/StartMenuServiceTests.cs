using Microsoft.Win32;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// «Персонализация», блок 2 — «Свой Пуск»: автозапуск с побайтным откатом, режим клавиши Win,
/// порядок «закрыть → положить → запустить». Реестр и запуск программы подменены — система не трогается.
/// </summary>
public sealed class StartMenuServiceTests : IDisposable
{
    private sealed class FakeRegistry : IThemeRegistry
    {
        public int Build { get; set; } = 26200;
        public Dictionary<string, (RegistryValueKind Kind, object Data)> Values { get; } = new();
        private static string K(string key, string name) => key + "|" + name;
        public (RegistryValueKind Kind, object Data)? Read(string key, string name) => Values.TryGetValue(K(key, name), out var v) ? v : null;
        public void Write(string key, string name, RegistryValueKind kind, object data) => Values[K(key, name)] = (kind, data);
        public void Delete(string key, string name) => Values.Remove(K(key, name));
        public void NotifyColors() { }
        public string? Run => Read(StartMenuService.RunKey, StartMenuService.RunName)?.Data as string;
    }

    private sealed class FakeHost : IStartMenuHost
    {
        public List<string> Calls { get; } = new();
        public bool StartResult { get; set; } = true;
        public bool Running { get; set; }
        public void Install(string source, string destination)
        {
            Calls.Add("install");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }
        public bool Start(string exe, string arguments) { Calls.Add("start " + arguments); Running = StartResult; return StartResult; }
        public void Stop() { Calls.Add("stop"); Running = false; }
        public bool Show() { Calls.Add("show"); return Running; }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v4t-start-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_dir, "client", StartMenuService.Exe);
    private string Installed => Path.Combine(_dir, "profile", StartMenuService.Exe);

    public StartMenuServiceTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
        File.WriteAllText(Source, "exe");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private StartMenuService Make(FakeRegistry reg, FakeHost host) =>
        new(Path.Combine(_dir, "start_menu.json"), reg, host, Installed, Source);

    [Fact]
    public void Enable_StopsOldCopy_Installs_WritesRun_Starts()
    {
        var reg = new FakeRegistry(); var host = new FakeHost();
        Assert.True(Make(reg, host).Enable(winKey: false));
        Assert.Equal(new[] { "stop", "install", "start " }, host.Calls);
        Assert.True(File.Exists(Installed));
        Assert.Equal($"\"{Installed}\"", reg.Run);
    }

    [Fact]
    public void WinKey_GoesToRunAndStartArguments()
    {
        var reg = new FakeRegistry(); var host = new FakeHost();
        var s = Make(reg, host);
        s.Enable(winKey: true);
        Assert.Equal($"\"{Installed}\" {StartMenuService.WinKeyArg}", reg.Run);
        Assert.Contains("start " + StartMenuService.WinKeyArg, host.Calls);
        Assert.True(s.Current.WinKey);
    }

    [Fact]
    public void Disable_RestoresForeignRunValue()
    {
        var reg = new FakeRegistry(); var host = new FakeHost();
        reg.Write(StartMenuService.RunKey, StartMenuService.RunName, RegistryValueKind.String, "чужое значение");
        var s = Make(reg, host);
        s.Enable(winKey: false);
        s.Enable(winKey: true);   // повторное включение не должно перезаписать «как было»
        s.Disable();
        Assert.Equal("чужое значение", reg.Run);
        Assert.False(s.Current.Enabled);
    }

    [Fact]
    public void Disable_RemovesRunValueThatWasAbsent()
    {
        var reg = new FakeRegistry(); var host = new FakeHost();
        var s = Make(reg, host);
        s.Enable(winKey: false);
        s.Revert(out var message);
        Assert.Null(reg.Run);
        Assert.Null(message);
        Assert.False(s.HasUndo);
    }

    [Fact]
    public void Disable_WhenNeverEnabled_LeavesRegistryAlone()
    {
        var reg = new FakeRegistry(); var host = new FakeHost();
        reg.Write(StartMenuService.RunKey, StartMenuService.RunName, RegistryValueKind.String, "чужое значение");
        Make(reg, host).Disable();
        Assert.Equal("чужое значение", reg.Run);
    }

    [Fact]
    public void Enable_WithoutProgram_Throws_AndChangesNothing()
    {
        File.Delete(Source);
        var reg = new FakeRegistry(); var host = new FakeHost();
        var s = Make(reg, host);
        Assert.Throws<FileNotFoundException>(() => s.Enable(winKey: false));
        Assert.Null(reg.Run);
        Assert.False(s.Current.Enabled);
    }

    [Fact]
    public void Enable_WhenStartFails_StillRegistersAutostart()
    {
        var reg = new FakeRegistry(); var host = new FakeHost { StartResult = false };
        var s = Make(reg, host);
        Assert.False(s.Enable(winKey: false));
        Assert.NotNull(reg.Run);
        Assert.True(s.Current.Enabled);
    }

    [Fact]
    public void ShowMenu_StartsWithShow_WhenEnabledButNotRunning()
    {
        var reg = new FakeRegistry(); var host = new FakeHost();
        var s = Make(reg, host);
        Assert.False(s.ShowMenu());   // выключен — не запускаем
        s.Enable(winKey: true);
        host.Running = false;         // человек закрыл программу из трея
        host.Calls.Clear();
        Assert.True(s.ShowMenu());
        Assert.Equal(new[] { "show", $"start {StartMenuService.WinKeyArg} --show" }, host.Calls);
    }

    [Fact]
    public void RefreshCopyIfStale_ReplacesOldCopyOnly()
    {
        var reg = new FakeRegistry(); var host = new FakeHost();
        var s = Make(reg, host);
        s.RefreshCopyIfStale();
        Assert.Empty(host.Calls);                 // выключен — ничего не делаем
        s.Enable(winKey: false);
        host.Calls.Clear();
        s.RefreshCopyIfStale();
        Assert.Empty(host.Calls);                 // копия та же — не перезапускаем
        File.WriteAllText(Source, "exe новой версии");
        s.RefreshCopyIfStale();
        Assert.Equal(new[] { "stop", "install", "start " }, host.Calls);
        Assert.Equal("exe новой версии", File.ReadAllText(Installed));
    }

    [Fact]
    public void Command_QuotesPathWithSpaces()
    {
        Assert.Equal("\"C:\\Users\\Иван Петров\\Ven4Start.exe\"", StartMenuService.Command(@"C:\Users\Иван Петров\Ven4Start.exe", false));
    }
}
