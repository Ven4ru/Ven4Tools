using System.IO;
using System.Threading.Tasks;

namespace Ven4Tools.Helpers;

/// <summary>
/// Atomic file write via temp-file + replace, preventing data loss on crash or power failure.
/// Each call uses a unique temp filename to avoid races between concurrent writes to the same path.
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

    public static async Task WriteAllTextAtomicAsync(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + "." + Path.GetRandomFileName() + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}
