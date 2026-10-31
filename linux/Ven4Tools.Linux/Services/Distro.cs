namespace Ven4Tools.Linux.Services;

// Дистрибутив по /etc/os-release и системный менеджер пакетов. Нужен в двух местах:
// показать, где запущена программа, и поставить сам Flatpak, если его нет
// (на Ubuntu из коробки нет; в Linux Mint и Fedora он уже стоит).
public static class Distro
{
    public static string Name { get; }
    public static string Id { get; }
    public static IReadOnlyList<string> IdLike { get; }

    static Distro()
    {
        // /usr/lib/os-release — запасное место по спецификации, если в /etc файла нет.
        var fields = Read(File.Exists("/etc/os-release") ? "/etc/os-release" : "/usr/lib/os-release");
        Name = fields.GetValueOrDefault("PRETTY_NAME") ?? fields.GetValueOrDefault("NAME") ?? "Linux";
        Id = fields.GetValueOrDefault("ID") ?? "";
        IdLike = (fields.GetValueOrDefault("ID_LIKE") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    public static bool Is(string id) => Id == id || IdLike.Contains(id);

    // Команда установки пакета под root. Порядок: сначала то, что подходит по ID дистрибутива,
    // затем любой найденный менеджер — на случай производных систем без ID_LIKE.
    public static string[]? InstallCommand(string package)
    {
        if ((Is("debian") || Is("ubuntu")) && Shell.Exists("apt-get")) return new[] { "apt-get", "install", "-y", package };
        if ((Is("fedora") || Is("rhel")) && Shell.Exists("dnf")) return new[] { "dnf", "install", "-y", package };
        if (Is("suse") && Shell.Exists("zypper")) return new[] { "zypper", "--non-interactive", "install", package };
        if (Is("arch") && Shell.Exists("pacman")) return new[] { "pacman", "-S", "--noconfirm", package };
        if (Shell.Exists("apt-get")) return new[] { "apt-get", "install", "-y", package };
        if (Shell.Exists("dnf")) return new[] { "dnf", "install", "-y", package };
        if (Shell.Exists("zypper")) return new[] { "zypper", "--non-interactive", "install", package };
        if (Shell.Exists("pacman")) return new[] { "pacman", "-S", "--noconfirm", package };
        return null;
    }

    // Строки вида KEY=value или KEY="value"; комментарии и пустые строки пропускаются.
    internal static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1];
            result[line[..eq]] = value;
        }
        return result;
    }

    private static Dictionary<string, string> Read(string path)
    {
        try { return Parse(File.ReadLines(path)); }
        catch (IOException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
    }
}
