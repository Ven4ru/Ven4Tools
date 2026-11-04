using Microsoft.Win32;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Блок 1 «Персонализации»: готовые наборы ссылаются только на существующие настройки, набор
/// «Без рекламы и Bing» применяется и откатывается побайтно, край панели Windows 10 — любой из четырёх.
/// Реестр подменён — настоящая система не трогается.
/// </summary>
public sealed class TaskbarStartPresetsTests : IDisposable
{
    private sealed class FakeSystem : ITaskbarSystem
    {
        public int Build { get; set; } = 26200;
        public Dictionary<string, int> Values { get; } = new();
        public bool AutoHide { get; set; }
        public int? ReadDword(string key, string name) => Values.TryGetValue(key + "|" + name, out var v) ? v : null;
        public void WriteDword(string key, string name, int value) => Values[key + "|" + name] = value;
        public void DeleteValue(string key, string name) => Values.Remove(key + "|" + name);
        public void NotifyChanged() { }
        public void RestartExplorer() { }
        public string Dump() => string.Join(";", Values.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value));
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v4t-presets-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void EveryPresetValue_IsAKnownSettingAndChoice()
    {
        foreach (var (title, _, values) in TaskbarStartService.Presets)
            foreach (var (id, v) in values)
            {
                var s = TaskbarStartService.All.FirstOrDefault(x => x.Id == id);
                Assert.True(s != null, $"набор «{title}»: нет настройки {id}");
                Assert.True(s!.Choices.Any(c => c.Value == v), $"набор «{title}»: у {id} нет значения {v}");
            }
    }

    [Fact]
    public void SettingIds_AreUnique()
    {
        var dup = TaskbarStartService.All.GroupBy(s => s.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dup);
    }

    [Theory]
    [InlineData(26200)]
    [InlineData(19045)]
    public void AdFreePreset_AppliesAndRevertsByteForByte(int build)
    {
        var sys = new FakeSystem { Build = build };
        sys.WriteDword(@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 1);   // часть значений есть, часть нет
        var before = sys.Dump();
        var svc = new TaskbarStartService(Path.Combine(_dir, $"undo{build}.json"), sys);
        var preset = TaskbarStartService.Presets.First(p => p.Title == "Без рекламы и Bing");
        var r = svc.Apply(preset.Values);
        Assert.Empty(r.Blocked);
        Assert.Equal(0, sys.ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled"));
        Assert.Equal(1, sys.ReadDword(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions"));
        Assert.Equal(0, sys.ReadDword(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled"));
        svc.RevertAll();
        Assert.Equal(before, sys.Dump());
    }

    [Fact]
    public void Windows10Taskbar_CanGoToAnyEdge_AndBack()
    {
        var reg = new ThemeReg { Build = 19045 };
        var settings = Enumerable.Range(0, 48).Select(i => (byte)i).ToArray(); settings[12] = 3;
        reg.Write(TaskbarThemeService.StuckRects, "Settings", RegistryValueKind.Binary, settings);
        var before = Convert.ToHexString((byte[])reg.Read(TaskbarThemeService.StuckRects, "Settings")!.Value.Data);
        var svc = new TaskbarThemeService(Path.Combine(_dir, "edge.json"), reg);
        foreach (var e in new[] { 1, 2, 0, 1 })
        {
            Assert.True(svc.SetEdge(e));
            Assert.Equal(e, svc.Edge);
        }
        Assert.True(svc.SetEdge(3));   // снова снизу — побайтно как было, откат больше не нужен
        Assert.Equal(before, Convert.ToHexString((byte[])reg.Read(TaskbarThemeService.StuckRects, "Settings")!.Value.Data));
        Assert.Null(svc.Current.LeftUndo);
    }

    private sealed class ThemeReg : IThemeRegistry
    {
        public int Build { get; set; }
        private readonly Dictionary<string, (RegistryValueKind, object)> _v = new();
        public (RegistryValueKind Kind, object Data)? Read(string key, string name) => _v.TryGetValue(key + "|" + name, out var x) ? x : null;
        public void Write(string key, string name, RegistryValueKind kind, object data) => _v[key + "|" + name] = (kind, data is byte[] b ? (byte[])b.Clone() : data);
        public void Delete(string key, string name) => _v.Remove(key + "|" + name);
        public void NotifyColors() { }
    }
}
