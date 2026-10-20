using Ven4Tools.Services.WindowsUpdate;

namespace Ven4Tools.Tests;

/// <summary>
/// Сброс Центра обновления: порядок «остановить → переименовать → запустить» и откат,
/// если служба не остановилась или вторая папка не переименовалась. Службы и папки
/// подменены — настоящая система не трогается.
/// </summary>
public sealed class WindowsUpdateResetServiceTests
{
    private const string Sd = @"C:\Windows\SoftwareDistribution";
    private const string Cr = @"C:\Windows\System32\catroot2";

    private sealed class FakeEnv : IWuResetEnvironment
    {
        public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase) { "wuauserv", "bits", "cryptsvc" };
        public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase)
            { "wuauserv", "bits", "cryptsvc", "msiserver", "usosvc", "dosvc" };
        public HashSet<string> Unstoppable { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase) { Sd, Cr };
        public HashSet<string> UnmovableFrom { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Log { get; } = new();

        public DateTime Now { get; } = new(2026, 10, 21, 15, 30, 0);
        public void Sleep(TimeSpan delay) { }
        public bool ServiceExists(string service) => Existing.Contains(service);
        public bool IsServiceRunning(string service) => Running.Contains(service);
        public bool IsServiceDisabled(string service) => false;
        public IReadOnlyList<string> RunningDependents(string service) => Array.Empty<string>();

        public bool StopService(string service, TimeSpan timeout)
        {
            Log.Add("stop " + service);
            if (Unstoppable.Contains(service)) return false;
            Running.Remove(service);
            return true;
        }

        public bool StartService(string service, TimeSpan timeout)
        {
            Log.Add("start " + service);
            Running.Add(service);
            return true;
        }

        public bool DirectoryExists(string path) => Dirs.Contains(path);

        public void MoveDirectory(string from, string to)
        {
            Log.Add($"move {from} -> {to}");
            if (UnmovableFrom.Contains(from)) throw new IOException("занято");
            Dirs.Remove(from);
            Dirs.Add(to);
        }
    }

    [Fact]
    public void ИмяКопии_СДатой_ИСуффиксомПриСовпадении()
    {
        var now = new DateTime(2026, 10, 21, 15, 30, 0);
        Assert.Equal(Sd + ".old-20261021-153000",
            WindowsUpdateResetService.BuildBackupPath(Sd, now, _ => false));

        var taken = new HashSet<string> { Sd + ".old-20261021-153000" };
        Assert.Equal(Sd + ".old-20261021-153000-2",
            WindowsUpdateResetService.BuildBackupPath(Sd + "\\", now, taken.Contains));
    }

    [Fact]
    public void Успех_ПапкиПереименованы_СлужбыЗапущены()
    {
        var env = new FakeEnv();
        var result = new WindowsUpdateResetService(env, Sd, Cr).Reset();

        Assert.True(result.Success);
        Assert.Equal(2, result.Renamed.Count);
        Assert.DoesNotContain(Sd, env.Dirs);
        Assert.DoesNotContain(Cr, env.Dirs);
        Assert.Contains(Sd + ".old-20261021-153000", env.Dirs);
        Assert.Contains(Cr + ".old-20261021-153000", env.Dirs);
        Assert.Contains("wuauserv", env.Running);
        Assert.Contains("cryptsvc", env.Running);
        Assert.Contains("bits", env.Running);
        Assert.Empty(result.NotStarted);

        // Переименование — строго после остановки служб.
        int lastStop = env.Log.FindLastIndex(l => l.StartsWith("stop "));
        int firstMove = env.Log.FindIndex(l => l.StartsWith("move "));
        Assert.True(lastStop < firstMove);
    }

    [Fact]
    public void СлужбаНеОстанавливается_ПапкиНеТронуты_СлужбыЗапущеныОбратно()
    {
        var env = new FakeEnv();
        env.Unstoppable.Add("cryptsvc");
        var result = new WindowsUpdateResetService(env, Sd, Cr).Reset();

        Assert.False(result.Success);
        Assert.Contains("cryptsvc", result.Message);
        Assert.DoesNotContain(env.Log, l => l.StartsWith("move "));
        Assert.Contains(Sd, env.Dirs);
        Assert.Contains("wuauserv", env.Running);
        Assert.Contains("bits", env.Running);
    }

    [Fact]
    public void ВтораяПапкаНеПереименовалась_ПерваяВозвращена()
    {
        var env = new FakeEnv();
        env.UnmovableFrom.Add(Cr);
        var result = new WindowsUpdateResetService(env, Sd, Cr).Reset();

        Assert.False(result.Success);
        Assert.Contains(Sd, env.Dirs);
        Assert.Contains(Cr, env.Dirs);
        Assert.Equal(2, env.Dirs.Count);
        Assert.Contains("возвращены", result.Message);
        Assert.Contains("cryptsvc", env.Running);
    }

    [Fact]
    public void НетПапок_УспехБезПереименований()
    {
        var env = new FakeEnv();
        env.Dirs.Clear();
        var result = new WindowsUpdateResetService(env, Sd, Cr).Reset();

        Assert.True(result.Success);
        Assert.Empty(result.Renamed);
    }
}
