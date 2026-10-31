using System.Reflection;
using System.Text.Json;

namespace Ven4Tools.Linux.Services;

// «Персонализация» клиента Windows для Linux: обои и экран блокировки с Венчиком.
// Настройки меняются через gsettings (GNOME, Ubuntu, Cinnamon, MATE, Budgie) или plasma-apply-wallpaperimage (KDE).
// Прежние значения сохраняются, и «Вернуть как было» ставит их обратно.
public enum LookPart { Wallpaper, LockScreen }

public static class Personalization
{
    private sealed record Key(string Schema, string Name, bool IsUri);

    private static string Dir => Directory.CreateDirectory(Path.Combine(
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
        "ven4tools", "personalization")).FullName;

    private static string UndoPath => Path.Combine(AppPaths.State, "personalization_undo.json");

    private static readonly Dictionary<LookPart, Key[]> Keys = new()
    {
        [LookPart.Wallpaper] = new[]
        {
            new Key("org.gnome.desktop.background", "picture-uri", true),
            new Key("org.gnome.desktop.background", "picture-uri-dark", true),
            new Key("org.cinnamon.desktop.background", "picture-uri", true),
            new Key("org.mate.background", "picture-filename", false),
        },
        [LookPart.LockScreen] = new[]
        {
            new Key("org.gnome.desktop.screensaver", "picture-uri", true),
            new Key("org.cinnamon.desktop.screensaver", "picture-uri", true),
        },
    };

    private static readonly Key[] Options =
    {
        new("org.gnome.desktop.background", "picture-options", false),
        new("org.gnome.desktop.screensaver", "picture-options", false),
        new("org.cinnamon.desktop.background", "picture-options", false),
    };

    public static bool IsKde => (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Contains("KDE", StringComparison.OrdinalIgnoreCase);

    // Ubuntu пишет «ubuntu:GNOME», чистый GNOME - «GNOME»
    public static bool IsGnome => (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Contains("GNOME", StringComparison.OrdinalIgnoreCase);

    public static bool IsSupported => Shell.Exists("gsettings") || (IsKde && Shell.Exists("plasma-apply-wallpaperimage"));

    public static bool HasUndo(LookPart part) => LoadUndo().ContainsKey(part.ToString());

    public static async Task<bool> ApplyAsync(LookPart part, Action<string> log)
    {
        var image = Extract(part);
        if (IsKde && part == LookPart.Wallpaper)
        {
            var (kde, _) = await Shell.RunAsync("plasma-apply-wallpaperimage", new[] { image }, log);
            return kde == 0;
        }

        var undo = LoadUndo();
        var saved = undo.TryGetValue(part.ToString(), out var old) ? old : new Dictionary<string, string>();
        var applied = 0;
        foreach (var key in Keys[part])
        {
            var current = await GetAsync(key);
            if (current is null) continue; // такой схемы в этом рабочем столе нет
            // Сохраняем самое первое значение: повторное «Применить» не должно затирать исходные обои
            saved.TryAdd(Id(key), current);
            var value = key.IsUri ? new Uri(image).AbsoluteUri : image.Replace("\\", "\\\\").Replace("'", "\\'");
            if (await SetAsync(key, $"'{value}'", log)) applied++;
        }
        foreach (var key in Options)
        {
            if (await GetAsync(key) is not { } current) continue;
            saved.TryAdd(Id(key), current);
            await SetAsync(key, "'zoom'", log);
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
            var dot = id.LastIndexOf('/');
            ok &= await SetAsync(new Key(id[..dot], id[(dot + 1)..], false), value, log);
        }
        undo.Remove(part.ToString());
        AppPaths.WriteAtomic(UndoPath, JsonSerializer.Serialize(undo));
        return ok;
    }

    // Картинка лежит внутри программы; на диск кладётся в профиль, туда же смотрят настройки
    private static string Extract(LookPart part)
    {
        var name = part == LookPart.Wallpaper ? "wallpaper.jpg" : "lockscreen.jpg";
        var path = Path.Combine(Dir, "venchik-" + name);
        using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("venchik/" + name)
            ?? throw new InvalidOperationException("Нет картинки " + name);
        using var dst = File.Create(path);
        src.CopyTo(dst);
        return path;
    }

    private static string Id(Key k) => $"{k.Schema}/{k.Name}";

    private static async Task<string?> GetAsync(Key k)
    {
        if (!Shell.Exists("gsettings")) return null;
        var (code, output) = await Shell.RunAsync("gsettings", new[] { "get", k.Schema, k.Name });
        return code == 0 ? output.Trim() : null;
    }

    // Значение передаётся в синтаксисе GVariant, как его отдаёт «gsettings get»
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
