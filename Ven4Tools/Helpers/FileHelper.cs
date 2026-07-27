using System.IO;
using System.Threading.Tasks;

namespace Ven4Tools.Helpers;

/// <summary>
/// Атомарная запись файла через временный файл с последующей заменой — не теряет
/// данные при падении приложения или отключении питания.
/// Имя временного файла у каждого вызова своё, чтобы одновременные записи по одному
/// и тому же пути не мешали друг другу.
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
