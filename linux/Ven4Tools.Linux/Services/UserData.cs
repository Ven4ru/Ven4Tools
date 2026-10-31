using System.IO.Compression;
using System.Text.Json;

namespace Ven4Tools.Linux.Services;

public sealed class LinuxSettings
{
    public string? Language { get; set; }
}

// Пользовательский набор - те же поля, что Preset в клиенте Windows (presets.json)
public sealed class UserPreset
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Apps { get; set; } = new();
}

// Настройки, избранное и наборы. Имена и форматы favorites.json и presets.json
// совпадают с клиентом Windows, чтобы профиль переносился между системами.
public static class UserData
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions Relaxed = new() { PropertyNameCaseInsensitive = true };

    private static string SettingsPath => Path.Combine(AppPaths.Config, "settings.json");
    private static string FavoritesPath => Path.Combine(AppPaths.Config, "favorites.json");
    private static string PresetsPath => Path.Combine(AppPaths.Config, "presets.json");

    public static LinuxSettings Settings { get; private set; } = Read<LinuxSettings>(SettingsPath) ?? new();
    public static HashSet<string> Favorites { get; private set; } = new(Read<List<string>>(FavoritesPath) ?? new());
    public static List<UserPreset> Presets { get; private set; } = Read<List<UserPreset>>(PresetsPath) ?? new();

    public static void SaveSettings() => Write(SettingsPath, Settings);
    public static void SaveFavorites() => Write(FavoritesPath, Favorites.OrderBy(x => x).ToList());
    public static void SavePresets() => Write(PresetsPath, Presets);

    public static UserPreset AddPreset(string name, IEnumerable<string> appIds)
    {
        var p = new UserPreset
        {
            Id = Presets.Count == 0 ? 1 : Presets.Max(x => x.Id) + 1,
            Name = name,
            Apps = appIds.Distinct().ToList()
        };
        Presets.Add(p);
        SavePresets();
        return p;
    }

    public static void RemovePreset(UserPreset p)
    {
        Presets.Remove(p);
        SavePresets();
    }

    private static readonly string[] ProfileFiles = { "favorites.json", "presets.json" };
    private const long MaxEntrySize = 10 * 1024 * 1024;

    public static int ExportProfile(string zipPath)
    {
        SaveFavorites();
        SavePresets();
        var tmp = zipPath + "." + Path.GetRandomFileName() + ".tmp";
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            foreach (var name in ProfileFiles)
                zip.CreateEntryFromFile(Path.Combine(AppPaths.Config, name), name, CompressionLevel.Optimal);
        File.Move(tmp, zipPath, overwrite: true);
        return ProfileFiles.Length;
    }

    // Из архива Windows берутся только избранное и наборы; остальное (тема, порядок
    // источников winget/choco) к Linux не относится и пропускается.
    public static (int Favorites, int Presets) ImportProfile(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        List<string>? favs = null;
        List<UserPreset>? presets = null;
        foreach (var e in zip.Entries)
        {
            if (e.Length > MaxEntrySize) continue;
            if (e.FullName.Equals("favorites.json", StringComparison.OrdinalIgnoreCase))
                favs = ReadEntry<List<string>>(e);
            else if (e.FullName.Equals("presets.json", StringComparison.OrdinalIgnoreCase))
                presets = ReadEntry<List<UserPreset>>(e);
        }

        if (favs is not null)
        {
            Favorites.UnionWith(favs);
            SaveFavorites();
        }
        if (presets is not null)
        {
            // Наборы с тем же именем заменяются, новые добавляются
            foreach (var p in presets.Where(p => p.Name.Length > 0))
            {
                Presets.RemoveAll(x => x.Name == p.Name);
                p.Id = Presets.Count == 0 ? 1 : Presets.Max(x => x.Id) + 1;
                Presets.Add(p);
            }
            SavePresets();
        }
        return (favs?.Count ?? 0, presets?.Count ?? 0);
    }

    private static T? ReadEntry<T>(ZipArchiveEntry e)
    {
        using var s = e.Open();
        return JsonSerializer.Deserialize<T>(s, Relaxed);
    }

    private static T? Read<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Relaxed) : default; }
        catch { return default; }
    }

    private static void Write<T>(string path, T value) =>
        AppPaths.WriteAtomic(path, JsonSerializer.Serialize(value, Indented));
}
