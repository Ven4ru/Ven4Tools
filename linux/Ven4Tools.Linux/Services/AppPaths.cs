namespace Ven4Tools.Linux.Services;

// Каталоги по спецификации XDG: настройки, кэш и состояние раздельно.
public static class AppPaths
{
    private static string Xdg(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } v
            ? v
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback);

    public static string Config => Ensure(Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "ven4tools"));
    public static string Cache => Ensure(Path.Combine(Xdg("XDG_CACHE_HOME", ".cache"), "ven4tools"));
    public static string State => Ensure(Path.Combine(Xdg("XDG_STATE_HOME", Path.Combine(".local", "state")), "ven4tools"));

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Запись через временный файл: обрыв посреди записи не оставляет битый файл
    public static void WriteAtomic(string path, string text)
    {
        var tmp = path + "." + Path.GetRandomFileName() + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
