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
    /// <summary>
    /// Проверка каталога и целевого файла на подмену reparse point'ом — та же
    /// защита, что уже стояла у <c>AppLogger</c> и журнала установки, но применённая
    /// в общем месте, а не в каждом сервисе по отдельности.
    ///
    /// <para>Почему это нужно здесь: клиент работает elevated
    /// (<c>requireAdministrator</c> в манифесте), а почти все вызывающие пишут в
    /// <c>%LocalAppData%\Ven4Tools</c> — дерево, доступное на запись обычному
    /// процессу того же пользователя. Подменив сам КАТАЛОГ junction'ом на защищённое
    /// место, непривилегированный процесс перенаправил бы туда elevated-запись:
    /// временный файл создаётся внутри каталога, и переименование остаётся в нём же.
    /// Это готовый примитив локального повышения привилегий, поэтому проверка идёт
    /// непосредственно перед записью, а не один раз при инициализации.</para>
    ///
    /// <para>Отказ здесь — исключение, а не тихий пропуск (в отличие от журналов, где
    /// потеря строки лога безобидна): вызывающие сохраняют пользовательские данные и
    /// уже обязаны переживать исключения записи, а молчаливая потеря настроек была бы
    /// хуже видимой ошибки.</para>
    /// </summary>
    private static void EnsureNotRedirected(string dir, string path)
    {
        if (PathHelper.IsReparsePoint(dir))
            throw new IOException($"Каталог подменён ссылкой, запись отменена: {dir}");
        if (PathHelper.IsReparsePoint(path))
            throw new IOException($"Файл подменён ссылкой, запись отменена: {path}");
    }

    public static void WriteAllTextAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        EnsureNotRedirected(dir, path);
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
        EnsureNotRedirected(dir, path);
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

    public static async Task WriteAllBytesAtomicAsync(string path, byte[] content)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        EnsureNotRedirected(dir, path);
        var tmp = path + "." + Path.GetRandomFileName() + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}
