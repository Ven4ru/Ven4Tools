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
    ///
    /// <para>Проверяется не только ближайший каталог, но и все его предки внутри
    /// %LocalAppData%: у снапшотов, иконок и т.п. путь вида
    /// <c>%LocalAppData%\Ven4Tools\snapshots\x.json</c>, и junction на месте самого
    /// <c>Ven4Tools</c> иначе проходил бы проверку — атрибуты <c>snapshots</c>
    /// читаются уже в цели подмены, где это обычный каталог. Выше %LocalAppData%
    /// не поднимаемся: перенос профиля junction'ом — легитимная настройка машины,
    /// и отказ там сломал бы клиенту всю запись.</para>
    /// </summary>
    private static void EnsureNotRedirected(string dir, string path)
    {
        foreach (var d in DirectoriesToCheck(dir))
        {
            if (PathHelper.IsReparsePoint(d))
                throw new IOException($"Каталог подменён ссылкой, запись отменена: {d}");
        }
        if (PathHelper.IsReparsePoint(path))
            throw new IOException($"Файл подменён ссылкой, запись отменена: {path}");
    }

    private static System.Collections.Generic.IEnumerable<string> DirectoriesToCheck(string dir)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        yield return full;

        string root = Path.TrimEndingDirectorySeparator(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData));
        if (root.Length == 0 ||
            !full.StartsWith(root + Path.DirectorySeparatorChar, System.StringComparison.OrdinalIgnoreCase))
            yield break;

        for (string? d = Path.GetDirectoryName(full);
             d != null && d.Length > root.Length;
             d = Path.GetDirectoryName(d))
        {
            yield return d;
        }
    }

    /// <summary>
    /// Проверка до <see cref="Directory.CreateDirectory(string)"/> обязательна:
    /// через подменённого предка создание недостающих каталогов уже само по себе
    /// создавало бы их elevated-процессом в цели подмены.
    /// </summary>
    private static void PrepareDirectory(string dir, string path)
    {
        EnsureNotRedirected(dir, path);
        Directory.CreateDirectory(dir);
        EnsureNotRedirected(dir, path);
    }

    public static void WriteAllTextAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        PrepareDirectory(dir, path);
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
        PrepareDirectory(dir, path);
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
        PrepareDirectory(dir, path);
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
