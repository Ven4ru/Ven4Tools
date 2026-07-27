using System.IO;

namespace Ven4Tools.Launcher.Helpers;

/// <summary>
/// Атомарная запись файла через временный файл с последующей заменой — не теряет
/// данные при падении приложения или отключении питания.
/// Повторяет Ven4Tools.Helpers.FileHelper (клиент): сборки разные, механизм один и тот же
/// (File.Move с перезаписью, не File.Replace), чтобы это был действительно один паттерн.
/// Асинхронной перегрузки здесь нет — лаунчеру она пока не нужна.
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
