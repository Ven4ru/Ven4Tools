using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// «Персонализация»: что пункт меняет при применении и что возвращает «Вернуть как было».
/// Реестр и вызовы Windows подменены — настоящая система не трогается.
/// </summary>
public sealed class PersonalizationServiceTests : IDisposable
{
    private sealed class FakeSystem : IPersonalizationSystem
    {
        public Dictionary<string, string> Strings { get; } = new();
        public Dictionary<string, int> Dwords { get; } = new();
        public List<(PersonalizationPart Part, string? Arg)> Broadcasts { get; } = new();

        private static string Key(string path, string name) => path + "|" + name;

        public string? ReadString(string path, string name) => Strings.TryGetValue(Key(path, name), out var v) ? v : null;
        public void WriteString(string path, string name, string value) => Strings[Key(path, name)] = value;
        public (bool Exists, int Value) ReadDword(string path, string name) =>
            Dwords.TryGetValue(Key(path, name), out var v) ? (true, v) : (false, 0);
        public void WriteDword(string path, string name, int value) => Dwords[Key(path, name)] = value;
        public void DeleteValue(string path, string name) { Strings.Remove(Key(path, name)); Dwords.Remove(Key(path, name)); }
        public void Broadcast(PersonalizationPart part, string? arg) => Broadcasts.Add((part, arg));

        public string? Get(string path, string name) => ReadString(path, name);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v4t-pers-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSystem _system = new();
    private readonly PersonalizationService _service;

    public PersonalizationServiceTests()
    {
        var source = Path.Combine(_dir, "source");
        Directory.CreateDirectory(source);
        foreach (var file in PersonalizationService.CursorFiles.Values
                     .Concat(PersonalizationService.SoundFiles.Values)
                     .Concat(new[] { PersonalizationService.WallpaperFile, PersonalizationService.LockScreenFile, PersonalizationService.ScreensaverFile })
                     .Distinct())
            File.WriteAllText(Path.Combine(source, file), file);

        _service = new PersonalizationService(source, Path.Combine(_dir, "user"), Path.Combine(_dir, "shared"),
            Path.Combine(_dir, "undo.json"), _system);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Cursors_AppliedAndRestored_IncludingValuesThatDidNotExist()
    {
        _system.WriteString(PersonalizationService.Cursors, "Arrow", @"%SystemRoot%\cursors\aero_arrow.cur");
        _system.WriteString(PersonalizationService.Cursors, "", "Windows Default");

        _service.Apply(PersonalizationPart.Cursors);

        Assert.True(_service.IsApplied(PersonalizationPart.Cursors));
        Assert.EndsWith("paw-arrow.cur", _system.Get(PersonalizationService.Cursors, "Arrow"));
        Assert.EndsWith("venchik-wait.ani", _system.Get(PersonalizationService.Cursors, "Wait"));
        Assert.Equal("Venchik", _system.Get(PersonalizationService.Cursors, ""));
        Assert.True(File.Exists(_system.Get(PersonalizationService.Cursors, "Arrow")));

        _service.Revert(PersonalizationPart.Cursors);

        Assert.False(_service.IsApplied(PersonalizationPart.Cursors));
        Assert.Equal(@"%SystemRoot%\cursors\aero_arrow.cur", _system.Get(PersonalizationService.Cursors, "Arrow"));
        Assert.Equal("Windows Default", _system.Get(PersonalizationService.Cursors, ""));
        Assert.Null(_system.Get(PersonalizationService.Cursors, "Wait"));
        Assert.Equal(2, _system.Broadcasts.Count(b => b.Part == PersonalizationPart.Cursors));
    }

    [Fact]
    public void SecondApply_DoesNotOverwriteRememberedState()
    {
        _system.WriteString(PersonalizationService.Desktop, "Wallpaper", @"C:\old.jpg");
        _service.Apply(PersonalizationPart.Wallpaper);
        // Windows сама пишет путь к новым обоям — второе «Применить» не должно счесть его прежним.
        _system.WriteString(PersonalizationService.Desktop, "Wallpaper", @"C:\venchik.jpg");
        _service.Apply(PersonalizationPart.Wallpaper);

        _service.Revert(PersonalizationPart.Wallpaper);

        Assert.Equal(@"C:\old.jpg", _system.Get(PersonalizationService.Desktop, "Wallpaper"));
        Assert.Equal(@"C:\old.jpg", _system.Broadcasts.Last().Arg);
    }

    [Fact]
    public void LockScreen_WritesPolicyAndRemovesItOnRevert()
    {
        _service.Apply(PersonalizationPart.LockScreen);

        var image = _system.Get(PersonalizationService.LockScreenCsp, "LockScreenImagePath");
        Assert.NotNull(image);
        Assert.StartsWith(Path.Combine(_dir, "shared"), image);
        Assert.Equal(1, _system.ReadDword(PersonalizationService.LockScreenCsp, "LockScreenImageStatus").Value);

        _service.Revert(PersonalizationPart.LockScreen);

        Assert.Null(_system.Get(PersonalizationService.LockScreenCsp, "LockScreenImagePath"));
        Assert.False(_system.ReadDword(PersonalizationService.LockScreenCsp, "LockScreenImageStatus").Exists);
    }

    [Fact]
    public void Screensaver_KeepsUserTimeout_AndRestoresInactiveState()
    {
        _system.WriteString(PersonalizationService.Desktop, "ScreenSaveActive", "0");
        _system.WriteString(PersonalizationService.Desktop, "ScreenSaveTimeOut", "300");

        _service.Apply(PersonalizationPart.Screensaver);

        Assert.Equal("1", _system.Get(PersonalizationService.Desktop, "ScreenSaveActive"));
        Assert.Equal("300", _system.Get(PersonalizationService.Desktop, "ScreenSaveTimeOut"));
        Assert.NotNull(_system.Get(PersonalizationService.Desktop, "SCRNSAVE.EXE"));

        _service.Revert(PersonalizationPart.Screensaver);

        Assert.Equal("0", _system.Get(PersonalizationService.Desktop, "ScreenSaveActive"));
        Assert.Null(_system.Get(PersonalizationService.Desktop, "SCRNSAVE.EXE"));
        Assert.Equal("0", _system.Broadcasts.Last().Arg);
    }

    [Fact]
    public void Sounds_SetSchemeAndRestore()
    {
        var evt = PersonalizationService.SoundEvents + @"\SystemAsterisk\.Current";
        _system.WriteString(evt, "", @"C:\Windows\media\Windows Background.wav");

        _service.Apply(PersonalizationPart.Sounds);
        Assert.EndsWith("info.wav", _system.Get(evt, ""));

        _service.Revert(PersonalizationPart.Sounds);
        Assert.Equal(@"C:\Windows\media\Windows Background.wav", _system.Get(evt, ""));
        Assert.Null(_system.Get(PersonalizationService.SoundEvents + @"\DeviceConnect\.Current", ""));
    }

    [Fact]
    public void Revert_WithoutApply_ChangesNothing()
    {
        _system.WriteString(PersonalizationService.Desktop, "Wallpaper", @"C:\old.jpg");
        _service.Revert(PersonalizationPart.Wallpaper);
        Assert.Equal(@"C:\old.jpg", _system.Get(PersonalizationService.Desktop, "Wallpaper"));
        Assert.Empty(_system.Broadcasts);
    }

    [Fact]
    public void MissingFiles_ApplyFailsBeforeTouchingSystem()
    {
        File.Delete(Path.Combine(_dir, "source", PersonalizationService.WallpaperFile));

        Assert.False(_service.IsAvailable(PersonalizationPart.Wallpaper));
        Assert.Throws<FileNotFoundException>(() => _service.Apply(PersonalizationPart.Wallpaper));
        Assert.False(_service.IsApplied(PersonalizationPart.Wallpaper));
        Assert.Empty(_system.Strings);
    }

    [Fact]
    public void ShippedAssets_CoverEveryPart()
    {
        // Файлы, которые кладёт рядом с программой сборка, — все, что ждёт сервис.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ven4Tools.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var root = Path.Combine(dir!.FullName, "Ven4Tools", "Personalization", "Venchik");
        var shipped = new PersonalizationService(root, _dir, _dir, Path.Combine(_dir, "u.json"), _system);
        foreach (var part in new[] { PersonalizationPart.Cursors, PersonalizationPart.Wallpaper, PersonalizationPart.LockScreen, PersonalizationPart.Sounds })
            Assert.True(shipped.IsAvailable(part), part.ToString());
    }
}
