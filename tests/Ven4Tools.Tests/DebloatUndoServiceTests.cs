using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Точечный откат твиков «Очистки»: что запоминается перед применением и что
/// возвращается. Реестр и службы подменены — настоящая система не трогается.
/// </summary>
public sealed class DebloatUndoServiceTests : IDisposable
{
    private sealed class FakeSystem : IDebloatSystemState
    {
        public Dictionary<string, int> Registry { get; } = new();
        public Dictionary<string, int> Services { get; } = new();
        public bool FailServiceRestore { get; set; }

        private static string Key(string path, string name) => path + "|" + name;

        public (bool Exists, int Value) ReadDword(string path, string name) =>
            Registry.TryGetValue(Key(path, name), out int value) ? (true, value) : (false, 0);

        public bool WriteDword(string path, string name, int value) { Registry[Key(path, name)] = value; return true; }

        public bool DeleteValue(string path, string name) { Registry.Remove(Key(path, name)); return true; }

        public int? ReadServiceStartMode(string service) =>
            Services.TryGetValue(service, out int mode) ? mode : null;

        public Task<bool> SetServiceStartModeAsync(string service, int mode, CancellationToken ct)
        {
            if (FailServiceRestore) return Task.FromResult(false);
            Services[service] = mode;
            return Task.FromResult(true);
        }

        public bool Has(string path, string name) => Registry.ContainsKey(Key(path, name));
        public int Get(string path, string name) => Registry[Key(path, name)];
    }

    private const string PolicyPath = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection";
    private const string AdsPath = @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v4t-undo-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly FakeSystem _system = new();
    private readonly DebloatUndoService _undo;

    public DebloatUndoServiceTests()
    {
        _path = Path.Combine(_dir, "debloat_undo.json");
        _undo = new DebloatUndoService(_path, _system);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly DebloatRegistryChange[] AdsTweak = { new(AdsPath, "Enabled", 0) };

    [Fact]
    public async Task ЗначениеБыло_ВозвращаетсяПрежнее()
    {
        _system.WriteDword(AdsPath, "Enabled", 1);

        _undo.Capture("advertising_id", AdsTweak, service: null);
        _system.WriteDword(AdsPath, "Enabled", 0);          // твик применён
        Assert.True(_undo.CanUndo("advertising_id"));

        Assert.True(await _undo.UndoAsync("advertising_id"));

        Assert.Equal(1, _system.Get(AdsPath, "Enabled"));
        Assert.False(_undo.CanUndo("advertising_id"));
    }

    [Fact]
    public async Task ЗначенияНеБыло_ОткатЕгоУдаляет_АНеОставляетНоль()
    {
        _undo.Capture("telemetry", new[] { new DebloatRegistryChange(PolicyPath, "AllowTelemetry", 0) }, service: null);
        _system.WriteDword(PolicyPath, "AllowTelemetry", 0);

        Assert.True(await _undo.UndoAsync("telemetry"));

        Assert.False(_system.Has(PolicyPath, "AllowTelemetry"));
    }

    [Fact]
    public async Task ПовторноеПрименение_НеПодменяетИсходноеСостояние()
    {
        _system.WriteDword(AdsPath, "Enabled", 1);
        _undo.Capture("advertising_id", AdsTweak, service: null);
        _system.WriteDword(AdsPath, "Enabled", 0);

        // Твик применили ещё раз: «прежним» не должно стать уже изменённое значение.
        _undo.Capture("advertising_id", AdsTweak, service: null);
        await _undo.UndoAsync("advertising_id");

        Assert.Equal(1, _system.Get(AdsPath, "Enabled"));
    }

    [Fact]
    public async Task Служба_ВозвращаетсяВПрежнийРежимЗапуска()
    {
        _system.Services["SysMain"] = 2;

        _undo.Capture("svc_sysmain", Array.Empty<DebloatRegistryChange>(), "SysMain");
        _system.Services["SysMain"] = 4;                     // отключена твиком

        Assert.True(await _undo.UndoAsync("svc_sysmain"));
        Assert.Equal(2, _system.Services["SysMain"]);
    }

    [Fact]
    public async Task НеудачныйОткат_ОставляетЗапись_ЧтобыМожноБылоПовторить()
    {
        _system.Services["DiagTrack"] = 2;
        _undo.Capture("svc_diagtrack", Array.Empty<DebloatRegistryChange>(), "DiagTrack");
        _system.FailServiceRestore = true;

        Assert.False(await _undo.UndoAsync("svc_diagtrack"));

        Assert.True(_undo.CanUndo("svc_diagtrack"));
    }

    [Fact]
    public void УдалениеПриложения_НеДаётЗаписиОтката()
    {
        _undo.Capture("Microsoft.BingNews", Array.Empty<DebloatRegistryChange>(), service: null);

        Assert.False(_undo.CanUndo("Microsoft.BingNews"));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void СлужбыНетВСистеме_ВозвращатьНечего()
    {
        _undo.Capture("svc_dmwappushsvc", Array.Empty<DebloatRegistryChange>(), "dmwappushservice");

        Assert.False(_undo.CanUndo("svc_dmwappushsvc"));
    }

    [Fact]
    public void ЗаписиПереживаютПерезапуск()
    {
        _system.WriteDword(AdsPath, "Enabled", 1);
        _undo.Capture("advertising_id", AdsTweak, service: null);

        var afterRestart = new DebloatUndoService(_path, _system);

        Assert.Equal(new[] { "advertising_id" }, afterRestart.RecordedTweaks());
    }

    [Fact]
    public async Task ИспорченныйФайл_НеРоняет()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_path, "не json");

        Assert.Empty(_undo.RecordedTweaks());
        Assert.False(await _undo.UndoAsync("advertising_id"));
    }

    [Theory]
    [InlineData("privacy", "telemetry", true)]
    [InlineData("privacy", "diag_track", true)]
    [InlineData("service", "svc_sysmain", true)]
    [InlineData("app", "Microsoft.BingNews", false)]
    [InlineData("privacy", "неизвестный", false)]
    public void ОткатываютсяТвикиРеестраИСлужб_НоНеУдалениеПриложений(string category, string id, bool expected) =>
        Assert.Equal(expected, DebloatTweakExecutor.IsUndoable(category, id));

    [Theory]
    [InlineData("SysMain; Remove-Item C:\\", 2)]     // имя службы из файла записей — не команда
    [InlineData("SysMain", 7)]                         // неизвестный режим запуска
    public async Task ОткатСлужбы_ОтклоняетНедопустимыеДанные(string service, int mode) =>
        Assert.False(await DebloatTweakExecutor.RestoreServiceAsync(service, mode));
}
