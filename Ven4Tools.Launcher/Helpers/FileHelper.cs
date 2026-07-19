using System.IO;

namespace Ven4Tools.Launcher.Helpers;

/// <summary>
/// Atomic file write via temp-file + replace, preventing data loss on crash or power failure.
/// Mirrors Ven4Tools.Helpers.FileHelper (client) byte-for-byte — separate assemblies, same
/// mechanism (File.Move overwrite, not File.Replace) so the two really are one pattern.
/// </summary>
internal static class FileHelper
{
    public static void WriteAllTextAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + "." + Path.GetRandomFileName() + ".tmp";
        try
        {
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}
