using Microsoft.Win32;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Общий учёт владения значениями реестра между «Очисткой», «Панелью задач и Пуском» и темой
/// панели: в каком бы порядке разделы ни применяли и ни возвращали одно и то же значение,
/// последний откат возвращает настоящий оригинал пользователя — побайтно, с видом значения.
/// И испорченные файлы отката: откладываются в .bad, «Применить» отказывает, данные не теряются.
/// Реестр подменён одним общим хранилищем — настоящая система не трогается.
/// </summary>
public sealed class RegistryOwnershipTests : IDisposable
{
    /// <summary>Один «реестр» на все разделы; пути разной записи («HKCU:\SOFTWARE\…», «Software\…») — одно значение.</summary>
    private sealed class Store
    {
        public Dictionary<string, (RegistryValueKind Kind, object Data)> Values { get; } = new();
        private static string K(string path, string name) => RegistryOwnership.ValueKey(path, name);
        public (RegistryValueKind Kind, object Data)? Read(string path, string name) => Values.TryGetValue(K(path, name), out var v) ? v : null;
        public void Write(string path, string name, RegistryValueKind kind, object data) => Values[K(path, name)] = (kind, data is byte[] b ? (byte[])b.Clone() : data);
        public void Delete(string path, string name) => Values.Remove(K(path, name));
        public int? Dword(string path, string name) => Read(path, name) is { Kind: RegistryValueKind.DWord, Data: int v } ? v : null;
        public string Dump() => string.Join(";", Values.OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}={kv.Value.Kind}:{(kv.Value.Data is byte[] b ? Convert.ToHexString(b) : kv.Value.Data)}"));
    }

    private sealed class DebloatSys(Store s) : IDebloatSystemState
    {
        public (bool Exists, int Value) ReadDword(string path, string name) => s.Dword(path, name) is int v ? (true, v) : (false, 0);
        public bool WriteDword(string path, string name, int value) { s.Write(path, name, RegistryValueKind.DWord, value); return true; }
        public bool DeleteValue(string path, string name) { s.Delete(path, name); return true; }
        public int? ReadServiceStartMode(string service) => null;
        public Task<bool> SetServiceStartModeAsync(string service, int mode, CancellationToken ct) => Task.FromResult(true);
        public (RegistryValueKind Kind, object Data)? ReadRaw(string path, string name) => s.Read(path, name);
        public bool WriteRaw(string path, string name, RegistryValueKind kind, object data) { s.Write(path, name, kind, data); return true; }
    }

    private sealed class TaskbarSys(Store s) : ITaskbarSystem
    {
        public int Build => 26200;
        public int? ReadDword(string key, string name) => s.Dword(key, name);
        public void WriteDword(string key, string name, int value) => s.Write(key, name, RegistryValueKind.DWord, value);
        public void DeleteValue(string key, string name) => s.Delete(key, name);
        public bool AutoHide { get; set; }
        public void NotifyChanged() { }
        public void RestartExplorer() { }
        public (RegistryValueKind Kind, object Data)? ReadRaw(string key, string name) => s.Read(key, name);
        public void WriteRaw(string key, string name, RegistryValueKind kind, object data) => s.Write(key, name, kind, data);
    }

    private sealed class ThemeReg(Store s) : IThemeRegistry
    {
        public int Build => 26200;
        public (RegistryValueKind Kind, object Data)? Read(string key, string name) => s.Read(key, name);
        public void Write(string key, string name, RegistryValueKind kind, object data) => s.Write(key, name, kind, data);
        public void Delete(string key, string name) => s.Delete(key, name);
        public void NotifyColors() { }
    }

    private const string Search = @"Software\Microsoft\Windows\CurrentVersion\Search";
    private const string ExplorerPolicy = @"Software\Policies\Microsoft\Windows\Explorer";
    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v4t-owners-" + Guid.NewGuid().ToString("N"));
    private readonly Store _reg = new();
    private readonly RegistryOwnership _owners;

    public RegistryOwnershipTests() => _owners = new RegistryOwnership(Path.Combine(_dir, "registry_owners.json"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private DebloatUndoService Debloat() => new(Path.Combine(_dir, "debloat_undo.json"), new DebloatSys(_reg), _owners);
    private TaskbarStartService Taskbar() => new(Path.Combine(_dir, "taskbar_undo.json"), new TaskbarSys(_reg), _owners);
    private TaskbarThemeService Theme() => new(Path.Combine(_dir, "taskbar_theme.json"), new ThemeReg(_reg), _owners);

    /// <summary>Как это делает «Очистка»: запомнить «как было», затем записать значения твика.</summary>
    private void ApplyDebloat(string tweakId)
    {
        var changes = DebloatTweakExecutor.RegistryChangesOf("ads", tweakId);
        Assert.NotEmpty(changes);
        Assert.True(Debloat().Capture(tweakId, changes, service: null));
        foreach (var c in changes) _reg.Write(c.Path, c.Name, RegistryValueKind.DWord, c.Value);
    }

    /// <summary>Исходное состояние пользователя: часть значений есть (одно — даже не DWORD), части нет.</summary>
    private void UserOriginal()
    {
        _reg.Write(Search, "BingSearchEnabled", RegistryValueKind.DWord, 1);
        _reg.Write(Cdm, "SubscribedContent-338388Enabled", RegistryValueKind.String, "1");   // вид тоже должен вернуться
        // DisableSearchBoxSuggestions, SubscribedContent-310093Enabled, ScoobeSystemSettingEnabled — нет
    }

    [Fact]
    public async Task ОчисткаПотомПанель_ВозвратОчистки_ОставляетПанель_ВозвратПанели_Оригинал()
    {
        UserOriginal();
        var before = _reg.Dump();

        ApplyDebloat("ads_search_web");     // Bing 0, DisableSearchBoxSuggestions 1
        ApplyDebloat("ads_start_suggestions");
        // Панель: «Bing» и «Предложения» — те же значения (берутся во владение без записи),
        // «Веб-подсказки» — другое значение, чем у «Очистки».
        var r = Taskbar().Apply(new Dictionary<string, int> { ["Bing"] = 0, ["StartSuggestions"] = 0, ["WebSuggest"] = 0 });
        Assert.Empty(r.Blocked);
        Assert.Equal(0, _reg.Dword(ExplorerPolicy, "DisableSearchBoxSuggestions"));

        Assert.True(await Debloat().UndoAsync("ads_search_web"));
        Assert.True(await Debloat().UndoAsync("ads_start_suggestions"));
        // Значения ещё держит панель — «Очистка» их не тронула.
        Assert.Equal(0, _reg.Dword(Search, "BingSearchEnabled"));
        Assert.Equal(0, _reg.Dword(ExplorerPolicy, "DisableSearchBoxSuggestions"));
        Assert.Equal(0, _reg.Dword(Cdm, "SubscribedContent-338388Enabled"));

        Taskbar().RevertAll();
        Assert.Equal(before, _reg.Dump());
        // Владельцев не осталось — учёт пуст.
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_dir, "registry_owners.json")).Trim());
    }

    [Fact]
    public async Task ПанельПотомОчистка_ВозвратПанели_ОставляетОчистку_ВозвратОчистки_Оригинал()
    {
        UserOriginal();
        var before = _reg.Dump();

        Taskbar().Apply(new Dictionary<string, int> { ["Bing"] = 0, ["StartSuggestions"] = 0, ["Welcome"] = 0, ["FinishSetup"] = 0 });
        ApplyDebloat("ads_search_web");
        ApplyDebloat("ads_start_suggestions");
        ApplyDebloat("ads_welcome");

        Taskbar().RevertAll();
        // «Очистка» ещё держит значения — панель их не вернула.
        Assert.Equal(0, _reg.Dword(Search, "BingSearchEnabled"));
        Assert.Equal(1, _reg.Dword(ExplorerPolicy, "DisableSearchBoxSuggestions"));
        Assert.Equal(0, _reg.Dword(Cdm, "SubscribedContent-310093Enabled"));

        foreach (var id in new[] { "ads_search_web", "ads_start_suggestions", "ads_welcome" })
            Assert.True(await Debloat().UndoAsync(id));
        Assert.Equal(before, _reg.Dump());
    }

    [Fact]
    public async Task ПанельПотомОчистка_ВозвратОчисткиПервым_ОставляетПанель_ЗатемОригинал()
    {
        UserOriginal();
        var before = _reg.Dump();
        Taskbar().Apply(new Dictionary<string, int> { ["Bing"] = 0 });
        ApplyDebloat("ads_search_web");
        Assert.True(await Debloat().UndoAsync("ads_search_web"));
        Assert.Equal(0, _reg.Dword(Search, "BingSearchEnabled"));   // панель ещё держит
        Taskbar().RevertAll();
        Assert.Equal(before, _reg.Dump());
    }

    [Fact]
    public void ТемаИПанель_ОбщийРежимWindows_ПоследнийВозвращаетОригинал()
    {
        _reg.Write(TaskbarThemeService.Personalize, "SystemUsesLightTheme", RegistryValueKind.DWord, 1);
        var before = _reg.Dump();

        Theme().ApplyTheme(TaskbarThemeService.Themes.First(t => t.Id == "venchik"));   // тёмный режим
        Taskbar().Apply(new Dictionary<string, int> { ["DarkSystem"] = 1 });           // человек выбрал светлый
        Theme().RevertAll();
        Assert.Equal(1, _reg.Dword(TaskbarThemeService.Personalize, "SystemUsesLightTheme"));   // держит панель
        Assert.Null(_reg.Read(TaskbarThemeService.Personalize, "ColorPrevalence"));             // только тема — уже возвращено
        Taskbar().RevertAll();
        Assert.Equal(before, _reg.Dump());
    }

    [Fact]
    public async Task ПотерянныйФайлОтката_НеДержитЗначениеВечно()
    {
        UserOriginal();
        var before = _reg.Dump();
        Taskbar().Apply(new Dictionary<string, int> { ["Bing"] = 0 });
        File.Delete(Path.Combine(_dir, "taskbar_undo.json"));     // записи панели пропали, в учёте — «taskbar:Bing»
        ApplyDebloat("ads_search_web");                             // оригинал всё равно из учёта — 1, а не 0
        Taskbar().RevertAll();                                      // сверка снимает владельца без записи
        Assert.True(await Debloat().UndoAsync("ads_search_web"));
        Assert.Equal(before, _reg.Dump());
    }

    // ── испорченные файлы отката ─────────────────────────────────────

    [Fact]
    public void ПанельЗадач_ИспорченныйФайл_ОтказПрименить_ВернутьСнимаетОтказ()
    {
        var path = Path.Combine(_dir, "taskbar_undo.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{ испорчено");
        UserOriginal();
        var before = _reg.Dump();

        var ex = Assert.Throws<UndoFileBrokenException>(() => Taskbar().Apply(new Dictionary<string, int> { ["Bing"] = 0 }));
        Assert.Equal(path + ".bad", ex.BackupPath);
        Assert.Contains("сохранён как", ex.Message);
        Assert.Equal(before, _reg.Dump());
        Assert.Equal("{ испорчено", File.ReadAllText(path + ".bad"));
        // В следующий запуск — тоже отказ, пока .bad лежит.
        Assert.Throws<UndoFileBrokenException>(() => Taskbar().Apply(new Dictionary<string, int> { ["Bing"] = 0 }));

        var r = Taskbar().RevertAll();
        Assert.NotNull(r.Message);
        Assert.Contains("Вернуть нечего", r.Message);
        Assert.False(File.Exists(path + ".bad"));
        var kept = Directory.GetFiles(_dir, "taskbar_undo.json.bad-*").Single();
        Assert.Equal("{ испорчено", File.ReadAllText(kept));   // данные не потеряны

        Taskbar().Apply(new Dictionary<string, int> { ["Bing"] = 0 });
        Assert.Equal(0, _reg.Dword(Search, "BingSearchEnabled"));
        Taskbar().RevertAll();
        Assert.Equal(before, _reg.Dump());
    }

    [Fact]
    public void ТемаПанели_ИспорченныйФайл_ОтказПрименить_УдалениеBadСнимаетОтказ()
    {
        var path = Path.Combine(_dir, "taskbar_theme.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "[1,2");
        var before = _reg.Dump();
        var theme = TaskbarThemeService.Themes.First(t => t.Id == "sea");

        Assert.Throws<UndoFileBrokenException>(() => Theme().ApplyTheme(theme));
        Assert.Equal(path + ".bad", Theme().BrokenBackup);
        Assert.Equal(before, _reg.Dump());

        File.Delete(path + ".bad");
        Assert.Null(Theme().BrokenBackup);
        Theme().ApplyTheme(theme);
        Theme().RevertAll(out var message);
        Assert.Null(message);
        Assert.Equal(before, _reg.Dump());
    }

    [Fact]
    public void ТемаПанели_ВернутьПриИспорченномФайле_ЧестноГоворитЧтоНечего()
    {
        var path = Path.Combine(_dir, "taskbar_theme.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "не json");
        Assert.False(Theme().HasUndo);
        Theme().RevertAll(out var message);
        Assert.Contains("Вернуть нечего", message);
        Assert.Null(Theme().BrokenBackup);
        Assert.Single(Directory.GetFiles(_dir, "taskbar_theme.json.bad-*"));
    }

    [Fact]
    public void Персонализация_ИспорченныйФайл_ОтказПрименить_ВернутьСнимаетОтказ()
    {
        var source = Path.Combine(_dir, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, PersonalizationService.WallpaperFile), "jpg");
        var undo = Path.Combine(_dir, "personalization_undo.json");
        File.WriteAllText(undo, "{\"Wallpaper\": ");
        var sys = new PersSys();
        var svc = new PersonalizationService(source, Path.Combine(_dir, "user"), Path.Combine(_dir, "shared"), undo, sys);

        Assert.Throws<UndoFileBrokenException>(() => svc.Apply(PersonalizationPart.Wallpaper));
        Assert.Empty(sys.Strings);
        Assert.Equal(undo + ".bad", svc.BrokenBackup);

        Assert.Contains("Вернуть нечего", svc.AcknowledgeBroken());
        Assert.Null(svc.BrokenBackup);
        Assert.Null(svc.AcknowledgeBroken());
        Assert.Equal("{\"Wallpaper\": ", File.ReadAllText(Directory.GetFiles(_dir, "personalization_undo.json.bad-*").Single()));

        svc.Apply(PersonalizationPart.Wallpaper);
        Assert.True(svc.IsApplied(PersonalizationPart.Wallpaper));
    }

    [Fact]
    public void ОтложенныйФайл_НеЗатираетсяСледующимИспорченным()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "x.json");
        File.WriteAllText(path, "первый");
        Assert.Equal(path + ".bad", Ven4Tools.Helpers.FileHelper.SetAsideBroken(path));
        File.WriteAllText(path, "второй");
        var second = Ven4Tools.Helpers.FileHelper.SetAsideBroken(path);
        Assert.NotNull(second);
        Assert.NotEqual(path + ".bad", second);
        Assert.Equal("первый", File.ReadAllText(path + ".bad"));
        Assert.Equal("второй", File.ReadAllText(second!));
    }

    [Theory]
    [InlineData(@"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Search", "bingsearchenabled")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Search\", "BingSearchEnabled")]
    public void КлючЗначения_НеЗависитОтЗаписиПути(string path, string name) =>
        Assert.Equal(@"hkcu\software\microsoft\windows\currentversion\search|bingsearchenabled", RegistryOwnership.ValueKey(path, name));

    [Fact]
    public void Оригинал_ВозвращаетсяПобайтно_СВидомЗначения()
    {
        foreach (var value in new (RegistryValueKind, object)[]
                 {
                     (RegistryValueKind.DWord, -5), (RegistryValueKind.QWord, 1L << 40), (RegistryValueKind.Binary, new byte[] { 0, 1, 255 }),
                     (RegistryValueKind.String, "a b"), (RegistryValueKind.ExpandString, "%SystemRoot%\\x"), (RegistryValueKind.MultiString, new[] { "a", "b" }),
                 })
        {
            var back = RegistryRaw.From(value).ToValue()!.Value;
            Assert.Equal(value.Item1, back.Kind);
            Assert.Equal(value.Item2, back.Data);
        }
        Assert.Null(RegistryRaw.From(null).ToValue());
    }

    private sealed class PersSys : IPersonalizationSystem
    {
        public Dictionary<string, string> Strings { get; } = new();
        public string? ReadString(string path, string name) => Strings.TryGetValue(path + "|" + name, out var v) ? v : null;
        public void WriteString(string path, string name, string value) => Strings[path + "|" + name] = value;
        public (bool Exists, int Value) ReadDword(string path, string name) => (false, 0);
        public void WriteDword(string path, string name, int value) { }
        public void DeleteValue(string path, string name) => Strings.Remove(path + "|" + name);
        public void Broadcast(PersonalizationPart part, string? arg) { }
    }
}
