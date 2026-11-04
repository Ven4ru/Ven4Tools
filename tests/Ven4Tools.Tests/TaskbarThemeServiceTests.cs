using Microsoft.Win32;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// «Персонализация», блок 1 — цветовые темы родной панели: читаемость тем, автозатемнение своего
/// цвета, палитра акцента и побайтный откат. Реестр подменён — настоящая система не трогается.
/// </summary>
public sealed class TaskbarThemeServiceTests : IDisposable
{
    private sealed class FakeRegistry : IThemeRegistry
    {
        public int Build { get; set; } = 26200;
        public Dictionary<string, (RegistryValueKind Kind, object Data)> Values { get; } = new();
        public int Notifications;
        /// <summary>Запись этого значения падает — сбой посередине применения или отката.</summary>
        public string? FailOn { get; set; }
        private static string K(string key, string name) => key + "|" + name;
        public (RegistryValueKind Kind, object Data)? Read(string key, string name) => Values.TryGetValue(K(key, name), out var v) ? v : null;
        public void Write(string key, string name, RegistryValueKind kind, object data)
        {
            if (name == FailOn) throw new UnauthorizedAccessException("тест: запись запрещена");
            Values[K(key, name)] = (kind, data is byte[] b ? (byte[])b.Clone() : data);
        }
        public void Delete(string key, string name) => Values.Remove(K(key, name));
        public void NotifyColors() => Notifications++;

        public string Dump() => string.Join(";", Values.OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}={kv.Value.Kind}:{(kv.Value.Data is byte[] b ? Convert.ToHexString(b) : kv.Value.Data)}"));
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v4t-theme-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private TaskbarThemeService Make(FakeRegistry reg) => new(Path.Combine(_dir, "taskbar_theme.json"), reg);

    /// <summary>Исходное состояние «как у Windows 11 по умолчанию»: часть значений есть, части нет.</summary>
    private static FakeRegistry Windows()
    {
        var r = new FakeRegistry();
        r.Write(TaskbarThemeService.Accent, "AccentPalette", RegistryValueKind.Binary, Convert.FromHexString("99EBFF004CC2FF000091F8000078D4000067C000003E9200001A6800F7630C00"));
        r.Write(TaskbarThemeService.Accent, "AccentColorMenu", RegistryValueKind.DWord, -2852864);
        r.Write(TaskbarThemeService.Dwm, "AccentColor", RegistryValueKind.DWord, -2852864);
        r.Write(TaskbarThemeService.Personalize, "SystemUsesLightTheme", RegistryValueKind.DWord, 1);
        r.Write(TaskbarThemeService.Personalize, "EnableTransparency", RegistryValueKind.DWord, 1);
        // StartColorMenu, ColorizationColor, ColorizationAfterglow, ColorPrevalence — нет
        return r;
    }

    [Fact]
    public void EveryTheme_IsReadable()
    {
        Assert.Equal(9, TaskbarThemeService.Themes.Count);
        foreach (var t in TaskbarThemeService.Themes)
            Assert.True(TaskbarThemeService.Readability(t) >= TaskbarThemeService.MinContrast, $"{t.Title}: {TaskbarThemeService.Readability(t):0.0}");
    }

    [Fact]
    public void VenchikTheme_HasOwnInnerEarColor()
    {
        var v = TaskbarThemeService.Themes.First(t => t.Id == "venchik");
        Assert.Equal("Венчик", v.Title);
        Assert.NotEqual(v.Accent, v.Inner);
    }

    [Theory]
    [InlineData("#FFFFFF")]
    [InlineData("#FFE600")]
    [InlineData("#9FD3FF")]
    [InlineData("#7BE0A0")]
    public void LightCustomColor_IsDarkenedUntilReadable(string hex)
    {
        var bar = TaskbarThemeService.DarkenToRead(hex, out var changed);
        Assert.True(changed);
        Assert.True(TaskbarThemeService.Contrast(TaskbarThemeService.Rgb(bar), (255, 255, 255)) >= 4.5);
    }

    [Fact]
    public void DarkCustomColor_IsKept()
    {
        Assert.Equal("#102030", TaskbarThemeService.DarkenToRead("#102030", out var changed));
        Assert.False(changed);
    }

    [Fact]
    public void Palette_HasEightColorsWithAccentAtIndex3()
    {
        var t = TaskbarThemeService.Themes.First(x => x.Id == "ginger");
        var p = TaskbarThemeService.BuildPalette(t);
        Assert.Equal(32, p.Length);
        Assert.Equal(TaskbarThemeService.Rgb(t.Accent), (p[12], p[13], p[14]));
        Assert.Equal(TaskbarThemeService.Rgb(t.Bar), (p[20], p[21], p[22]));
    }

    [Fact]
    public void ApplyThenRevert_RestoresRegistryByteForByte()
    {
        var reg = Windows();
        var before = reg.Dump();
        var svc = Make(reg);
        svc.ApplyTheme(TaskbarThemeService.Themes.First(t => t.Id == "venchik"));
        Assert.NotEqual(before, reg.Dump());
        Assert.Equal(1, (int)reg.Read(TaskbarThemeService.Personalize, "ColorPrevalence")!.Value.Data);
        svc.RevertAll();
        Assert.Equal(before, reg.Dump());
        Assert.False(svc.HasUndo);
    }

    [Fact]
    public void SecondTheme_DoesNotOverwriteOriginal()
    {
        var reg = Windows();
        var before = reg.Dump();
        var svc = Make(reg);
        foreach (var t in TaskbarThemeService.Themes) svc.ApplyTheme(t);
        svc.ApplyTheme(TaskbarThemeService.FromCustom("#FFE600", true, out _));
        Assert.Equal("custom", svc.Current.ThemeId);
        svc.RevertAll();
        Assert.Equal(before, reg.Dump());
    }

    [Fact]
    public void FailureMidApply_KeepsUndo_AndRevertRestoresWhatWasWritten()
    {
        var reg = Windows();
        var before = reg.Dump();
        var svc = Make(reg);
        reg.FailOn = "ColorizationColor";   // первые значения уже записаны, дальше — сбой
        Assert.Throws<UnauthorizedAccessException>(() => svc.ApplyTheme(TaskbarThemeService.Themes.First(t => t.Id == "sea")));
        Assert.NotEqual(before, reg.Dump());
        reg.FailOn = null;
        Assert.True(Make(reg).HasUndo);      // «как было» успело попасть на диск
        Make(reg).RevertAll();
        Assert.Equal(before, reg.Dump());
    }

    [Fact]
    public void FailureMidRevert_RestoresTheRest_AndRetriesLater()
    {
        var reg = Windows();
        var before = reg.Dump();
        var svc = Make(reg);
        svc.ApplyTheme(TaskbarThemeService.Themes.First(t => t.Id == "cherry"));
        reg.FailOn = "AccentColorMenu";
        svc.RevertAll();
        Assert.True(svc.HasUndo);
        Assert.Single(svc.Current.ColorUndo);
        Assert.Equal(1, (int)reg.Read(TaskbarThemeService.Personalize, "SystemUsesLightTheme")!.Value.Data);
        reg.FailOn = null;
        svc.RevertAll();
        Assert.Equal(before, reg.Dump());
        Assert.False(svc.HasUndo);
    }

    [Fact]
    public void AppliedTheme_SurvivesRestart()
    {
        var reg = Windows();
        Make(reg).ApplyTheme(TaskbarThemeService.Themes.First(t => t.Id == "sea"));
        Assert.Equal("sea", Make(reg).AppliedTheme?.Id);
    }

    [Fact]
    public void TaskbarLeft_OnlyOnWindows10_AndRevertsWholeValue()
    {
        var settings = Enumerable.Range(0, 48).Select(i => (byte)i).ToArray();
        settings[12] = 3; // снизу

        var w11 = Windows();
        w11.Write(TaskbarThemeService.StuckRects, "Settings", RegistryValueKind.Binary, settings);
        var s11 = Make(w11);
        Assert.False(s11.CanMoveLeft);
        Assert.False(s11.SetLeft(true));

        var w10 = Windows(); w10.Build = 19045;
        w10.Write(TaskbarThemeService.StuckRects, "Settings", RegistryValueKind.Binary, settings);
        var before = w10.Dump();
        var s10 = new TaskbarThemeService(Path.Combine(_dir, "w10.json"), w10);
        Assert.True(s10.SetLeft(true));
        Assert.True(s10.IsLeft);
        Assert.True(s10.RevertAll());   // нужен перезапуск проводника
        Assert.Equal(before, w10.Dump());
        Assert.False(s10.IsLeft);
    }
}
