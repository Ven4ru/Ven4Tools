using System.IO;
using System.Threading.Tasks;

namespace Ven4Tools.Helpers;

/// <summary>
/// Atomic file write via temp-file + replace, preventing data loss on crash or power failure.
/// </summary>
internal static class FileHelper
{
    public static void WriteAllTextAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    public static async Task WriteAllTextAtomicAsync(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
