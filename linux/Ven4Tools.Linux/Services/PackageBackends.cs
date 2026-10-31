using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Ven4Tools.Linux.Services;

public enum BackendKind { Flatpak, Snap }

// Запуск внешних команд с построчной передачей вывода в журнал.
public static class Shell
{
    public static bool Exists(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, tool)));

    public static async Task<(int Code, string Output)> RunAsync(
        string file, IEnumerable<string> args, Action<string>? log = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Вывод утилит нужен на английском - так его проще разбирать
        psi.Environment["LC_ALL"] = "C";

        log?.Invoke($"$ {file} {string.Join(' ', psi.ArgumentList)}");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Не запустился {file}");
        var sb = new System.Text.StringBuilder();
        void OnLine(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (sb) sb.AppendLine(e.Data);
            if (!string.IsNullOrWhiteSpace(e.Data)) log?.Invoke(e.Data);
        }
        p.OutputDataReceived += OnLine;
        p.ErrorDataReceived += OnLine;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        return (p.ExitCode, sb.ToString());
    }

    // Несколько команд под root за один запрос пароля. В скрипт попадают только
    // имена пакетов, прошедшие проверку по шаблону, - подставить чужую команду нельзя.
    public static Task<(int Code, string Output)> RunAsRootAsync(IEnumerable<string> commands, Action<string> log) =>
        RunAsync("pkexec", new[] { "/bin/sh", "-c", string.Join(" && ", commands) }, log);
}

public static partial class Flatpak
{
    private const string FlathubRepo = "https://dl.flathub.org/repo/flathub.flatpakrepo";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex IdPattern();

    public static bool IsAvailable => Shell.Exists("flatpak");

    public static async Task<HashSet<string>> ListInstalledAsync()
    {
        if (!IsAvailable) return new();
        var (_, output) = await Shell.RunAsync("flatpak", new[] { "list", "--app", "--columns=application" });
        return Lines(output).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // Flatpak ставит в профиль пользователя (--user): пароль не нужен, вся очередь - одной командой
    public static async Task<bool> InstallAsync(IReadOnlyCollection<string> ids, Action<string> log)
    {
        if (ids.Count == 0) return true;
        Check(ids);
        var (add, _) = await Shell.RunAsync("flatpak",
            new[] { "remote-add", "--user", "--if-not-exists", "flathub", FlathubRepo }, log);
        if (add != 0) return false;
        var (code, _) = await Shell.RunAsync("flatpak",
            new[] { "install", "--user", "-y", "--noninteractive", "flathub" }.Concat(ids), log);
        return code == 0;
    }

    public static async Task<bool> RemoveAsync(IReadOnlyCollection<string> ids, Action<string> log)
    {
        if (ids.Count == 0) return true;
        Check(ids);
        var (code, _) = await Shell.RunAsync("flatpak",
            new[] { "uninstall", "--user", "-y", "--noninteractive" }.Concat(ids), log);
        return code == 0;
    }

    public static async Task<List<string>> ListUpdatesAsync()
    {
        if (!IsAvailable) return new();
        var (_, output) = await Shell.RunAsync("flatpak",
            new[] { "remote-ls", "--user", "--updates", "--app", "--columns=application" });
        return Lines(output).Where(l => IdPattern().IsMatch(l)).ToList();
    }

    public static async Task<bool> UpdateAllAsync(Action<string> log)
    {
        if (!IsAvailable) return true;
        var (code, _) = await Shell.RunAsync("flatpak", new[] { "update", "--user", "-y", "--noninteractive" }, log);
        return code == 0;
    }

    // На Ubuntu Flatpak из коробки нет: ставится системным менеджером пакетов с запросом
    // пароля (apt-get, dnf, zypper или pacman — что есть в этом дистрибутиве).
    public static async Task<bool> SetupAsync(Action<string> log)
    {
        var install = Distro.InstallCommand("flatpak");
        if (install is null)
        {
            log(L.T("Не найден менеджер пакетов: поставьте Flatpak средствами своей системы."));
            return false;
        }
        var (code, _) = await Shell.RunAsync("pkexec", install, log);
        return code == 0 && IsAvailable;
    }

    private static void Check(IEnumerable<string> ids)
    {
        foreach (var id in ids)
            if (!IdPattern().IsMatch(id)) throw new ArgumentException($"Недопустимый id flatpak: {id}");
    }

    private static IEnumerable<string> Lines(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public static partial class Snap
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex NamePattern();

    public static bool IsAvailable => Shell.Exists("snap");

    public static async Task<HashSet<string>> ListInstalledAsync()
    {
        if (!IsAvailable) return new();
        var (_, output) = await Shell.RunAsync("snap", new[] { "list" });
        return FirstColumn(output).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // Обычные пакеты ставятся одной командой; classic snap принимает только по одному
    // имени, поэтому каждый отдельной командой - но всё под одним запросом пароля.
    public static async Task<bool> InstallAsync(IReadOnlyCollection<(string Name, bool Classic)> snaps, Action<string> log)
    {
        if (snaps.Count == 0) return true;
        Check(snaps.Select(s => s.Name));
        var commands = new List<string>();
        var strict = snaps.Where(s => !s.Classic).Select(s => s.Name).ToList();
        if (strict.Count > 0) commands.Add("snap install " + string.Join(' ', strict));
        commands.AddRange(snaps.Where(s => s.Classic).Select(s => $"snap install --classic {s.Name}"));
        var (code, _) = await Shell.RunAsRootAsync(commands, log);
        return code == 0;
    }

    public static async Task<bool> RemoveAsync(IReadOnlyCollection<string> names, Action<string> log)
    {
        if (names.Count == 0) return true;
        Check(names);
        var (code, _) = await Shell.RunAsRootAsync(new[] { "snap remove " + string.Join(' ', names) }, log);
        return code == 0;
    }

    public static async Task<List<string>> ListUpdatesAsync()
    {
        if (!IsAvailable) return new();
        var (_, output) = await Shell.RunAsync("snap", new[] { "refresh", "--list" });
        // Без обновлений snap пишет одну строку «All snaps up to date.»
        return output.Contains("up to date", StringComparison.OrdinalIgnoreCase)
            ? new()
            : FirstColumn(output).Where(n => NamePattern().IsMatch(n)).ToList();
    }

    public static async Task<bool> UpdateAllAsync(Action<string> log)
    {
        if (!IsAvailable) return true;
        var (code, _) = await Shell.RunAsRootAsync(new[] { "snap refresh" }, log);
        return code == 0;
    }

    private static void Check(IEnumerable<string> names)
    {
        foreach (var n in names)
            if (!NamePattern().IsMatch(n)) throw new ArgumentException($"Недопустимое имя snap: {n}");
    }

    private static IEnumerable<string> FirstColumn(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")
            .Where(n => n.Length > 0);
}
