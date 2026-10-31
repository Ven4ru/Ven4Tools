namespace Ven4Tools.Linux.Services;

// Автозагрузка по спецификации XDG Autostart: файлы .desktop в /etc/xdg/autostart (система)
// и ~/.config/autostart (пользователь). Системные файлы не трогаем: чтобы выключить программу,
// кладём в профиль одноимённый файл с Hidden=true - так делают и настройки самих рабочих столов.
public sealed class AutostartEntry
{
    public required string FileName { get; init; }
    public required string Name { get; init; }
    public required string Command { get; init; }
    public bool IsSystem { get; init; }
    public bool IsEnabled { get; set; }
}

public static class Autostart
{
    private const string SystemDir = "/etc/xdg/autostart";
    private static string UserDir => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart");

    public static List<AutostartEntry> List()
    {
        var desktop = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries);
        var result = new Dictionary<string, AutostartEntry>();

        foreach (var (dir, isSystem) in new[] { (SystemDir, true), (UserDir, false) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var path in Directory.EnumerateFiles(dir, "*.desktop"))
            {
                var file = Path.GetFileName(path);
                var f = Parse(path);
                // Пользовательский файл перекрывает системный с тем же именем
                var system = isSystem || (result.TryGetValue(file, out var prev) && prev.IsSystem);
                if (!ShownIn(f, desktop) && !result.ContainsKey(file)) continue;
                if (isSystem && IsSystemPlumbing(f)) continue;
                result[file] = new AutostartEntry
                {
                    FileName = file,
                    Name = Get(f, "Name") ?? file,
                    Command = Get(f, "Exec") ?? "",
                    IsSystem = system,
                    IsEnabled = Get(f, "Hidden") != "true" && Get(f, "X-GNOME-Autostart-enabled") != "false",
                };
            }
        }
        return result.Values.OrderBy(e => e.Name, StringComparer.InvariantCultureIgnoreCase).ToList();
    }

    public static void SetEnabled(AutostartEntry entry, bool enabled)
    {
        Directory.CreateDirectory(UserDir);
        var userPath = Path.Combine(UserDir, entry.FileName);
        var source = File.Exists(userPath) ? userPath : Path.Combine(SystemDir, entry.FileName);
        var lines = File.ReadAllLines(source)
            .Where(l => !l.StartsWith("Hidden=") && !l.StartsWith("X-GNOME-Autostart-enabled="))
            .ToList();
        // Ключи - в секцию [Desktop Entry], сразу после её заголовка
        var at = lines.FindIndex(l => l.Trim() == "[Desktop Entry]") + 1;
        lines.Insert(at, $"Hidden={(enabled ? "false" : "true")}");
        lines.Insert(at + 1, $"X-GNOME-Autostart-enabled={(enabled ? "true" : "false")}");
        AppPaths.WriteAtomic(userPath, string.Join('\n', lines) + "\n");
        entry.IsEnabled = enabled;
    }

    // Служебные части рабочего стола (хранилище ключей, специальные возможности, уведомления)
    // не показываем: их выключение ломает систему, а не ускоряет вход. Признаки - из самих файлов:
    // скрытые из меню, запускаемые по условию, привязанные к фазе запуска сеанса или к одному рабочему столу,
    // и программы из /usr/libexec, которые человек сам не запускает.
    private static bool IsSystemPlumbing(Dictionary<string, string> f) =>
        f.GetValueOrDefault("NoDisplay") == "true"
        || f.ContainsKey("AutostartCondition")
        || f.ContainsKey("X-GNOME-AutoRestart")
        || f.ContainsKey("X-GNOME-Autostart-Phase")
        || f.ContainsKey("OnlyShowIn")
        || (f.GetValueOrDefault("Exec") ?? "").StartsWith("/usr/libexec/");

    private static bool ShownIn(Dictionary<string, string> f, string[] desktop)
    {
        if (Get(f, "OnlyShowIn") is { } only && !only.Split(';').Intersect(desktop, StringComparer.OrdinalIgnoreCase).Any())
            return false;
        if (Get(f, "NotShowIn") is { } not && not.Split(';').Intersect(desktop, StringComparer.OrdinalIgnoreCase).Any())
            return false;
        return true;
    }

    private static string? Get(Dictionary<string, string> f, string key)
    {
        // Сначала перевод под язык интерфейса (Name[ru]), затем общее значение
        var lang = L.IsEnglish ? "en" : "ru";
        return f.TryGetValue($"{key}[{lang}]", out var local) ? local : f.TryGetValue(key, out var v) ? v : null;
    }

    private static Dictionary<string, string> Parse(string path)
    {
        var map = new Dictionary<string, string>();
        var inEntry = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inEntry = line == "[Desktop Entry]"; continue; }
            if (!inEntry || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq > 0) map.TryAdd(line[..eq].Trim(), line[(eq + 1)..].Trim());
        }
        return map;
    }
}
