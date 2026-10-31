using Ven4Tools.Linux.Models;

namespace Ven4Tools.Linux.Services;

// Очередь: программы делятся по источнику, каждая группа ставится одной командой.
// Snap-группа целиком идёт под одним запросом пароля.
public static class InstallQueue
{
    public static async Task<bool> RunAsync(IReadOnlyCollection<AppItem> items, bool remove, Action<string> log)
    {
        var flatpaks = items.Where(i => i.Backend == BackendKind.Flatpak).Select(i => i.PackageId!).Distinct().ToList();
        var snaps = items.Where(i => i.Backend == BackendKind.Snap)
            .Select(i => (Name: i.PackageId!, Classic: i.Package!.SnapClassic))
            .DistinctBy(s => s.Name).ToList();

        var ok = true;
        if (flatpaks.Count > 0)
            ok &= remove ? await Flatpak.RemoveAsync(flatpaks, log) : await Flatpak.InstallAsync(flatpaks, log);
        if (snaps.Count > 0)
            ok &= remove ? await Snap.RemoveAsync(snaps.Select(s => s.Name).ToList(), log) : await Snap.InstallAsync(snaps, log);
        return ok;
    }
}
