using System.Reflection;
using System.Text.Json;

namespace Ven4Tools.Linux.Services;

// «Персонализация» клиента Windows для Linux: обои, экран блокировки, курсоры-лапы и звуки Венчика.
// Настройки меняются через gsettings (GNOME, Ubuntu, Cinnamon, MATE, Budgie), в KDE - его утилитами.
// Прежние значения сохраняются, и «Вернуть как было» ставит их обратно.
public enum LookPart { Wallpaper, LockScreen, Cursors, Sounds }

public static class Personalization
{
    private enum Kind { Uri, Path, Theme, Zoom, True, SoundFile }

    private sealed record Key(string Schema, string Name, Kind Kind, string Arg = "");

    private const string ThemeName = "Venchik";

    private static string DataHome => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    private static string Dir => Directory.CreateDirectory(Path.Combine(DataHome, "ven4tools", "personalization")).FullName;
    private static string CursorDir => Path.Combine(DataHome, "icons", ThemeName);
    private static string SoundDir => Path.Combine(DataHome, "sounds", ThemeName);

    private static string UndoPath => Path.Combine(AppPaths.State, "personalization_undo.json");

    private static readonly Dictionary<LookPart, Key[]> Keys = new()
    {
        [LookPart.Wallpaper] = new Key[]
        {
            new("org.gnome.desktop.background", "picture-uri", Kind.Uri),
            new("org.gnome.desktop.background", "picture-uri-dark", Kind.Uri),
            new("org.gnome.desktop.background", "picture-options", Kind.Zoom),
            new("org.cinnamon.desktop.background", "picture-uri", Kind.Uri),
            new("org.cinnamon.desktop.background", "picture-options", Kind.Zoom),
            new("org.mate.background", "picture-filename", Kind.Path),
        },
        [LookPart.LockScreen] = new Key[]
        {
            new("org.gnome.desktop.screensaver", "picture-uri", Kind.Uri),
            new("org.gnome.desktop.screensaver", "picture-options", Kind.Zoom),
            new("org.cinnamon.desktop.screensaver", "picture-uri", Kind.Uri),
        },
        [LookPart.Cursors] = new Key[]
        {
            new("org.gnome.desktop.interface", "cursor-theme", Kind.Theme),
            new("org.cinnamon.desktop.interface", "cursor-theme", Kind.Theme),
            new("org.mate.peripherals-mouse", "cursor-theme", Kind.Theme),
        },
        [LookPart.Sounds] = new Key[]
        {
            new("org.gnome.desktop.sound", "theme-name", Kind.Theme),
            new("org.gnome.desktop.sound", "event-sounds", Kind.True),
            new("org.mate.sound", "theme-name", Kind.Theme),
            new("org.mate.sound", "event-sounds", Kind.True),
            // Cinnamon берёт звуки не из темы, а из файлов по событиям
            new("org.cinnamon.sounds", "notification-file", Kind.SoundFile, "notify.wav"),
            new("org.cinnamon.sounds", "notification-enabled", Kind.True),
            new("org.cinnamon.sounds", "plug-file", Kind.SoundFile, "connect.wav"),
            new("org.cinnamon.sounds", "plug-enabled", Kind.True),
            new("org.cinnamon.sounds", "unplug-file", Kind.SoundFile, "disconnect.wav"),
            new("org.cinnamon.sounds", "unplug-enabled", Kind.True),
        },
    };

    // Курсоры: файл программы -> имена, под которыми их ищут разные программы (CSS, X11, Qt)
    private static readonly Dictionary<string, string[]> CursorNames = new()
    {
        ["default"] = new[] { "left_ptr", "arrow", "top_left_arrow" },
        ["pointer"] = new[] { "hand", "hand1", "hand2", "pointing_hand", "9d800788f1b08800ae810202380a0822", "e29285e634086352946a0e7090d73106" },
        ["help"] = new[] { "question_arrow", "whats_this", "left_ptr_help", "d9ce0ab605698f320427677b458ad60b", "5c6cd98b3f3ebcb1f9c7f1c204630408" },
        ["progress"] = new[] { "left_ptr_watch", "half-busy", "3ecb610c1bf2410f44200f48c40d3599", "08e8e1c95fe2fc01f976f1e063a24ccd", "00000000000000020006000e7e9ffc3f" },
        ["wait"] = new[] { "watch" },
    };

    // Звуки: файл программы -> события freedesktop, которые он озвучивает
    private static readonly Dictionary<string, string[]> SoundNames = new()
    {
        ["info.wav"] = new[] { "dialog-information", "complete" },
        ["warning.wav"] = new[] { "dialog-warning", "bell", "bell-window-system" },
        ["error.wav"] = new[] { "dialog-error" },
        ["notify.wav"] = new[] { "message", "message-new-instant", "message-new-email" },
        ["connect.wav"] = new[] { "device-added" },
        ["disconnect.wav"] = new[] { "device-removed" },
    };

    private static string Desktop => Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";

    public static bool IsKde => Desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase);

    // Ubuntu пишет «ubuntu:GNOME», чистый GNOME - «GNOME»
    public static bool IsGnome => Desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase);

    public static bool IsSupported(LookPart part) => IsKde
        ? part switch
        {
            LookPart.Wallpaper => Shell.Exists("plasma-apply-wallpaperimage"),
            LookPart.Cursors => Shell.Exists("plasma-apply-cursortheme"),
            _ => false,
        }
        : Shell.Exists("gsettings");

    public static bool HasUndo(LookPart part) => LoadUndo().ContainsKey(part.ToString());

    public static async Task<bool> ApplyAsync(LookPart part, Action<string> log)
    {
        var file = Install(part);
        if (IsKde)
            return await ApplyKdeAsync(part, file, log);

        var undo = LoadUndo();
        var saved = undo.TryGetValue(part.ToString(), out var old) ? old : new Dictionary<string, string>();
        var applied = 0;
        foreach (var key in Keys[part])
        {
            var current = await GetAsync(key);
            if (current is null) continue; // такой схемы в этом рабочем столе нет
            // Сохраняем самое первое значение: повторное «Применить» не должно затирать исходное оформление
            saved.TryAdd(Id(key), current);
            if (await SetAsync(key, Value(key, file), log) && key.Kind is not (Kind.Zoom or Kind.True)) applied++;
        }

        undo[part.ToString()] = saved;
        AppPaths.WriteAtomic(UndoPath, JsonSerializer.Serialize(undo));
        return applied > 0;
    }

    public static async Task<bool> RevertAsync(LookPart part, Action<string> log)
    {
        var undo = LoadUndo();
        if (!undo.TryGetValue(part.ToString(), out var saved)) return false;
        var ok = true;
        foreach (var (id, value) in saved)
        {
            var slash = id.LastIndexOf('/');
            if (id.StartsWith("kde:"))
                ok &= await RevertKdeAsync(id, value, log);
            else
                ok &= await SetAsync(new Key(id[..slash], id[(slash + 1)..], Kind.Path), value, log);
        }
        undo.Remove(part.ToString());
        AppPaths.WriteAtomic(UndoPath, JsonSerializer.Serialize(undo));
        return ok;
    }

    // Значение в синтаксисе GVariant, как его отдаёт «gsettings get»
    private static string Value(Key key, string file) => key.Kind switch
    {
        Kind.Uri => Quote(new Uri(file).AbsoluteUri),
        Kind.Path => Quote(file),
        Kind.Theme => Quote(ThemeName),
        Kind.Zoom => "'zoom'",
        Kind.True => "true",
        Kind.SoundFile => Quote(Path.Combine(SoundDir, "stereo", key.Arg)),
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private static string Quote(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    // KDE: обои и курсоры его же утилитами. Прежний курсор читаем из kcminputrc, чтобы вернуть
    private static async Task<bool> ApplyKdeAsync(LookPart part, string file, Action<string> log)
    {
        var undo = LoadUndo();
        var saved = undo.TryGetValue(part.ToString(), out var old) ? old : new Dictionary<string, string>();
        int code;
        if (part == LookPart.Cursors)
        {
            var current = Shell.Exists("kreadconfig6")
                ? (await Shell.RunAsync("kreadconfig6", new[] { "--file", "kcminputrc", "--group", "Mouse", "--key", "cursorTheme" })).Output
                : "";
            saved.TryAdd("kde:cursor", current.Trim() is { Length: > 0 } c ? c : "breeze_cursors");
            (code, _) = await Shell.RunAsync("plasma-apply-cursortheme", new[] { ThemeName }, log);
        }
        else if (part == LookPart.Wallpaper)
            (code, _) = await Shell.RunAsync("plasma-apply-wallpaperimage", new[] { file }, log);
        else
            return false;
        undo[part.ToString()] = saved;
        AppPaths.WriteAtomic(UndoPath, JsonSerializer.Serialize(undo));
        return code == 0;
    }

    private static async Task<bool> RevertKdeAsync(string id, string value, Action<string> log)
    {
        if (id != "kde:cursor") return true;
        var (code, _) = await Shell.RunAsync("plasma-apply-cursortheme", new[] { value }, log);
        return code == 0;
    }

    // Файлы лежат внутри программы; на диск кладутся в профиль пользователя, туда же смотрят настройки
    private static string Install(LookPart part)
    {
        switch (part)
        {
            case LookPart.Wallpaper:
            case LookPart.LockScreen:
                var name = part == LookPart.Wallpaper ? "wallpaper.jpg" : "lockscreen.jpg";
                var path = Path.Combine(Dir, "venchik-" + name);
                Extract("venchik/" + name, path);
                return path;

            case LookPart.Cursors:
                var cursors = Directory.CreateDirectory(Path.Combine(CursorDir, "cursors")).FullName;
                foreach (var (file, aliases) in CursorNames)
                {
                    Extract("venchik/cursors/" + file, Path.Combine(cursors, file));
                    foreach (var alias in aliases)
                    {
                        var link = Path.Combine(cursors, alias);
                        File.Delete(link);
                        File.CreateSymbolicLink(link, file);
                    }
                }
                // Чего нет у Венчика (стрелки изменения размера, текстовый курсор), берётся из стандартной темы
                AppPaths.WriteAtomic(Path.Combine(CursorDir, "index.theme"),
                    "[Icon Theme]\nName=Venchik\nComment=Ven4Tools\nInherits=Adwaita,breeze_cursors,DMZ-White,default\n");
                return CursorDir;

            case LookPart.Sounds:
                var stereo = Directory.CreateDirectory(Path.Combine(SoundDir, "stereo")).FullName;
                foreach (var (file, events) in SoundNames)
                {
                    Extract("venchik/sounds/" + file, Path.Combine(stereo, file));
                    foreach (var ev in events)
                    {
                        var link = Path.Combine(stereo, ev + ".wav");
                        File.Delete(link);
                        File.CreateSymbolicLink(link, file);
                    }
                }
                AppPaths.WriteAtomic(Path.Combine(SoundDir, "index.theme"),
                    "[Sound Theme]\nName=Venchik\nComment=Ven4Tools\nInherits=freedesktop\nDirectories=stereo\n\n[stereo]\nOutputProfile=stereo\n");
                return SoundDir;
        }
        throw new ArgumentOutOfRangeException(nameof(part));
    }

    public static IReadOnlyCollection<string> SoundFiles => SoundNames.Keys;

    // Прослушать звук до применения: тем же проигрывателем, что есть в системе (PipeWire, PulseAudio, ALSA)
    public static async Task PlayAsync(string file, Action<string> log)
    {
        var player = new[] { "pw-play", "paplay", "aplay" }.FirstOrDefault(Shell.Exists);
        if (player is null) { log(L.T("Нет проигрывателя звука (pw-play, paplay или aplay).")); return; }
        var path = Path.Combine(Dir, "preview-" + file);
        Extract("venchik/sounds/" + file, path);
        await Shell.RunAsync(player, new[] { path });
    }

    private static void Extract(string resource, string path)
    {
        using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Нет файла " + resource);
        using var dst = File.Create(path);
        src.CopyTo(dst);
    }

    private static string Id(Key k) => $"{k.Schema}/{k.Name}";

    private static async Task<string?> GetAsync(Key k)
    {
        if (!Shell.Exists("gsettings")) return null;
        var (code, output) = await Shell.RunAsync("gsettings", new[] { "get", k.Schema, k.Name });
        return code == 0 ? output.Trim() : null;
    }

    private static async Task<bool> SetAsync(Key k, string value, Action<string> log)
    {
        var (code, _) = await Shell.RunAsync("gsettings", new[] { "set", k.Schema, k.Name, value }, log);
        return code == 0;
    }

    private static Dictionary<string, Dictionary<string, string>> LoadUndo()
    {
        try
        {
            return File.Exists(UndoPath)
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(UndoPath)) ?? new()
                : new();
        }
        catch (JsonException) { return new(); }
    }
}
