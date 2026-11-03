using System.Text.RegularExpressions;

namespace Ven4Tools.Linux.Services;

// Очистка места, как «Место на диске» клиента Windows. Удаляется только то, что система
// сама создаёт заново: корзина, эскизы, кэш пакетов, старые записи журнала, ненужные среды Flatpak.
public sealed class CleanupItem
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Hint { get; init; }
    public bool NeedsRoot { get; init; }
    public long Size { get; set; } = -1;
    public bool IsSelected { get; set; } = true;
    public string SizeText => Size < 0 ? "" : SystemInfo.Size(Size);
}

public static partial class Cleanup
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string Trash => Path.Combine(Home, ".local", "share", "Trash");
    private static string Thumbnails => Path.Combine(Home, ".cache", "thumbnails");

    private static string? PackageCache =>
        Shell.Exists("apt-get") ? "/var/cache/apt/archives"
        : Shell.Exists("dnf") ? (Directory.Exists("/var/cache/libdnf5") ? "/var/cache/libdnf5" : "/var/cache/dnf")
        : null;

    public static async Task<List<CleanupItem>> ScanAsync()
    {
        var items = new List<CleanupItem>
        {
            new() { Key = "trash", Title = L.T("Корзина"), Hint = L.T("Удалённые файлы, которые ещё лежат в корзине"), Size = DirSize(Trash) },
            new() { Key = "thumbs", Title = L.T("Эскизы картинок"), Hint = L.T("Миниатюры файлов, создаются заново при просмотре папок"), Size = DirSize(Thumbnails) },
        };
        if (Flatpak.IsAvailable)
            items.Add(new() { Key = "flatpak", Title = L.T("Ненужные среды Flatpak"), Hint = L.T("Среды выполнения, которые больше не нужны ни одной программе") });
        if (PackageCache is { } cache)
            items.Add(new() { Key = "pkgcache", Title = L.T("Кэш пакетов системы"), Hint = L.T("Скачанные установщики уже установленных обновлений"), NeedsRoot = true, Size = DirSize(cache) });
        if (Shell.Exists("journalctl"))
            items.Add(new() { Key = "journal", Title = L.T("Журнал системы"), Hint = L.T("Записи старше последних 100 МБ"), NeedsRoot = true, Size = await JournalSizeAsync() });
        // Отмечено только то, где есть что убрать: журнал до 100 МБ очистка не уменьшит.
        // У Flatpak размер заранее не известен - пункт остаётся отмеченным
        foreach (var i in items)
            i.IsSelected = i.Key == "journal" ? i.Size > 100L << 20 : i.Size != 0;
        return items;
    }

    public static async Task<bool> RunAsync(IReadOnlyCollection<CleanupItem> selected, Action<string> log)
    {
        var ok = true;
        foreach (var item in selected.Where(i => !i.NeedsRoot))
        {
            switch (item.Key)
            {
                case "trash":
                    ClearDir(Path.Combine(Trash, "files"), log);
                    ClearDir(Path.Combine(Trash, "info"), log);
                    break;
                case "thumbs":
                    ClearDir(Thumbnails, log);
                    break;
                case "flatpak":
                    var (code, _) = await Shell.RunAsync("flatpak", new[] { "uninstall", "--user", "--unused", "-y", "--noninteractive" }, log);
                    ok &= code == 0;
                    break;
            }
        }

        // Всё, что требует root, - одним запросом пароля. Команды постоянные, внешних данных в них нет
        var root = new List<string>();
        if (selected.Any(i => i.Key == "pkgcache"))
            root.Add(Shell.Exists("apt-get") ? "apt-get clean" : "dnf clean packages");
        if (selected.Any(i => i.Key == "journal"))
            root.Add("journalctl --vacuum-size=100M");
        if (root.Count > 0)
        {
            var (code, _) = await Shell.RunAsRootAsync(root, log);
            ok &= code == 0;
        }
        return ok;
    }

    [GeneratedRegex(@"take up ([\d.]+)([KMGT]?)")]
    private static partial Regex JournalUsage();

    private static async Task<long> JournalSizeAsync()
    {
        var (_, output) = await Shell.RunAsync("journalctl", new[] { "--disk-usage" });
        var m = JournalUsage().Match(output);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return -1;
        var mult = m.Groups[2].Value switch { "K" => 1L << 10, "M" => 1L << 20, "G" => 1L << 30, "T" => 1L << 40, _ => 1L };
        return (long)(v * mult);
    }

    // Размер папки без перехода по ссылкам; недоступное для чтения просто не считается
    private static long DirSize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opts)) total += f.Length;
        }
        catch (Exception) { }
        return total;
    }

    private static void ClearDir(string dir, Action<string> log)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            try
            {
                if (entry is DirectoryInfo d && (d.Attributes & FileAttributes.ReparsePoint) == 0) d.Delete(recursive: true);
                else entry.Delete();
            }
            catch (Exception ex) { log($"{entry.FullName}: {ex.Message}"); }
        }
    }
}
