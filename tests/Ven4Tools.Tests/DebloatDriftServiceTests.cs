using Ven4Tools.Services;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

/// <summary>
/// Проверка твиков после обновления Windows: какие из применённых система вернула.
/// Реестр, службы и список пакетов подменены — настоящая система не трогается.
/// </summary>
public sealed class DebloatDriftServiceTests : IDisposable
{
    private sealed class FakeSystem : IDebloatSystemState
    {
        public Dictionary<string, int> Registry { get; } = new();
        public Dictionary<string, int> Services { get; } = new();

        private static string Key(string path, string name) => path + "|" + name;

        public void Set(string path, string name, int value) => Registry[Key(path, name)] = value;

        public (bool Exists, int Value) ReadDword(string path, string name) =>
            Registry.TryGetValue(Key(path, name), out int value) ? (true, value) : (false, 0);

        public bool WriteDword(string path, string name, int value) { Set(path, name, value); return true; }
        public bool DeleteValue(string path, string name) { Registry.Remove(Key(path, name)); return true; }

        public int? ReadServiceStartMode(string service) =>
            Services.TryGetValue(service, out int mode) ? mode : null;

        public Task<bool> SetServiceStartModeAsync(string service, int mode, CancellationToken ct)
        {
            Services[service] = mode;
            return Task.FromResult(true);
        }
    }

    private const string AdsPath = @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo";
    private const string SystemPolicyPath = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v4t-drift-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSystem _system = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private IReadOnlyList<string> Find(IReadOnlyCollection<string>? appx, params (string Category, string Id)[] applied) =>
        DebloatDriftService.FindDrifted(applied, _system, appx);

    [Fact]
    public void Твик_реестра_действует_пока_значение_совпадает_с_целевым()
    {
        _system.Set(AdsPath, "Enabled", 0);
        Assert.Empty(Find(null, ("privacy", "advertising_id")));
    }

    [Fact]
    public void Твик_реестра_вернулся_если_значение_изменено_или_удалено()
    {
        _system.Set(AdsPath, "Enabled", 1);
        Assert.Equal(new[] { "advertising_id" }, Find(null, ("privacy", "advertising_id")));

        _system.Registry.Clear();
        Assert.Equal(new[] { "advertising_id" }, Find(null, ("privacy", "advertising_id")));
    }

    [Fact]
    public void Твик_из_нескольких_значений_вернулся_если_сброшено_хотя_бы_одно()
    {
        _system.Set(SystemPolicyPath, "EnableActivityFeed", 0);
        _system.Set(SystemPolicyPath, "PublishUserActivities", 1);
        Assert.Equal(new[] { "activity_history" }, Find(null, ("privacy", "activity_history")));
    }

    [Fact]
    public void Служба_вернулась_если_она_снова_не_отключена()
    {
        _system.Services["SysMain"] = 4;
        Assert.Empty(Find(null, ("service", "svc_sysmain")));

        _system.Services["SysMain"] = 2;
        Assert.Equal(new[] { "svc_sysmain" }, Find(null, ("service", "svc_sysmain")));
    }

    [Fact]
    public void Службы_нет_в_системе_это_не_возврат_твика()
    {
        Assert.Empty(Find(null, ("service", "svc_dmwappushsvc")));
    }

    [Fact]
    public void Твик_приватности_со_службой_проверяется_по_службе()
    {
        _system.Services["DiagTrack"] = 2;
        Assert.Equal(new[] { "diag_track" }, Find(null, ("privacy", "diag_track")));
    }

    [Fact]
    public void Удалённое_приложение_вернулось_если_пакет_снова_установлен()
    {
        var appx = new[] { "Microsoft.WindowsCalculator", "microsoft.zunemusic" };
        Assert.Equal(new[] { "Microsoft.ZuneMusic" },
            Find(appx, ("app", "Microsoft.ZuneMusic"), ("app", "Microsoft.People")));
    }

    [Fact]
    public void Без_списка_пакетов_приложения_не_объявляются_вернувшимися()
    {
        Assert.Empty(Find(null, ("app", "Microsoft.ZuneMusic")));
    }

    [Fact]
    public void Журнал_запоминает_применённое_и_забывает_возвращённое()
    {
        string path = Path.Combine(_dir, "debloat_applied.json");
        var journal = new DebloatAppliedJournal(path);
        Assert.Empty(journal.AppliedTweaks());

        journal.MarkApplied("telemetry");
        journal.MarkApplied("Microsoft.People");
        journal.MarkApplied("TELEMETRY");

        var reread = new DebloatAppliedJournal(path);
        Assert.Equal(2, reread.AppliedTweaks().Count);

        reread.Forget("telemetry");
        Assert.Equal(new[] { "Microsoft.People" }, new DebloatAppliedJournal(path).AppliedTweaks());
    }

    [Fact]
    public void Повреждённый_журнал_читается_как_пустой()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "debloat_applied.json");
        File.WriteAllText(path, "{ это не json");
        Assert.Empty(new DebloatAppliedJournal(path).AppliedTweaks());
    }

    [Fact]
    public async Task Вкладка_помечает_вернувшиеся_твики_и_снимает_пометку_когда_они_снова_действуют()
    {
        _system.Set(AdsPath, "Enabled", 1);
        _system.Services["SysMain"] = 4;
        bool appxRequested = false;

        var vm = new DebloaterViewModel
        {
            DriftSystem = _system,
            AppliedTweaksSource = () => new[] { "advertising_id", "svc_sysmain", "неизвестный_твик" },
            InstalledAppxSource = _ => { appxRequested = true; return Task.FromResult<IReadOnlyCollection<string>?>(null); }
        };

        await vm.CheckDriftAsync(announce: true);

        Assert.True(vm.HasDrift);
        Assert.Contains("Рекламный идентификатор", vm.DriftText);
        Assert.DoesNotContain("SysMain", vm.DriftText);
        Assert.False(appxRequested, "Без применённых удалений приложений PowerShell запускаться не должен.");

        _system.Set(AdsPath, "Enabled", 0);
        await vm.CheckDriftAsync(announce: true);

        Assert.False(vm.HasDrift);
        Assert.Equal("", vm.DriftText);
        Assert.Contains("действуют", vm.StatusText);
    }

    [Fact]
    public async Task Вкладка_запрашивает_список_пакетов_только_при_применённых_удалениях()
    {
        int requests = 0;
        var vm = new DebloaterViewModel
        {
            DriftSystem = _system,
            AppliedTweaksSource = () => new[] { "Microsoft.People" },
            InstalledAppxSource = _ =>
            {
                requests++;
                return Task.FromResult<IReadOnlyCollection<string>?>(new[] { "Microsoft.People" });
            }
        };

        await vm.CheckDriftAsync(announce: false);

        Assert.Equal(1, requests);
        Assert.True(vm.HasDrift);
        Assert.Contains("People", vm.DriftText);
    }
}
