namespace Ven4Tools.Linux.Services;

// Сводка о компьютере, как на вкладке «Система» клиента Windows. Всё читается из /proc и /etc,
// внешние программы и права root не нужны.
public static class SystemInfo
{
    public sealed record Disk(string Mount, string Format, long Total, long Free);

    public static string Cpu()
    {
        var lines = ReadLines("/proc/cpuinfo");
        var model = lines.FirstOrDefault(l => l.StartsWith("model name"))?.Split(':', 2)[1].Trim() ?? "?";
        return $"{model}, {Environment.ProcessorCount} {L.T("потоков")}";
    }

    public static (long Total, long Available) Memory()
    {
        long Kb(string key) =>
            ReadLines("/proc/meminfo").FirstOrDefault(l => l.StartsWith(key + ":")) is { } l
            && long.TryParse(l.Split(':', 2)[1].Trim().Split(' ')[0], out var v) ? v * 1024 : 0;
        return (Kb("MemTotal"), Kb("MemAvailable"));
    }

    public static string Kernel() => Read("/proc/sys/kernel/osrelease").Trim();

    public static string Desktop() =>
        Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") is { Length: > 0 } d ? d : "?";

    public static TimeSpan Uptime() =>
        double.TryParse(Read("/proc/uptime").Split(' ')[0], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var s) ? TimeSpan.FromSeconds(s) : TimeSpan.Zero;

    // Только настоящие разделы: служебные tmpfs, snap-образы и прочие виртуальные системы не показываем
    private static readonly HashSet<string> RealFormats = new(StringComparer.OrdinalIgnoreCase)
        { "ext2", "ext3", "ext4", "btrfs", "xfs", "f2fs", "vfat", "exfat", "ntfs", "ntfs3", "fuseblk", "zfs" };

    public static List<Disk> Disks() =>
        DriveInfo.GetDrives()
            .Where(d => d.IsReady && RealFormats.Contains(d.DriveFormat) && !d.Name.StartsWith("/snap/")
                        && !d.Name.StartsWith("/boot/efi"))
            .Select(d => new Disk(d.Name, d.DriveFormat, d.TotalSize, d.AvailableFreeSpace))
            .GroupBy(d => (d.Total, d.Free)).Select(g => g.OrderBy(d => d.Mount.Length).First())
            .ToList();

    public static string Size(long bytes)
    {
        string[] units = L.IsEnglish ? new[] { "B", "KB", "MB", "GB", "TB" } : new[] { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double v = bytes; var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }

    private static string Read(string path)
    {
        try { return File.ReadAllText(path); } catch { return ""; }
    }

    private static string[] ReadLines(string path)
    {
        try { return File.ReadAllLines(path); } catch { return Array.Empty<string>(); }
    }
}
