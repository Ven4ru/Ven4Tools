using System.IO;

namespace Ven4Tools.Launcher.Helpers;

/// <summary>
/// Атомарная запись файла через временный файл с последующей заменой — не теряет
/// данные при падении приложения или отключении питания.
/// Повторяет Ven4Tools.Helpers.FileHelper (клиент): сборки разные, механизм один и тот же
/// (File.Move с перезаписью, не File.Replace, плюс проверка подмены reparse point'ом),
/// чтобы это был действительно один паттерн.
/// Асинхронной перегрузки здесь нет — лаунчеру она пока не нужна.
/// </summary>
internal static class FileHelper
{
    /// <summary>
    /// Является ли путь reparse point (junction/symlink). Не удалось определить —
    /// возвращаем true (fail-closed, вызывающий не пишет).
    /// Копия Ven4Tools.Helpers.PathHelper.IsReparsePoint из клиента: заводить в
    /// лаунчере отдельный PathHelper ради одного метода незачем, а общей библиотеки
    /// между проектами намеренно нет.
    /// </summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch { return true; }
    }

    /// <summary>
    /// Проверка каталога и целевого файла на подмену reparse point'ом — тот же guard,
    /// что у клиентского FileHelper.
    ///
    /// <para>Почему он нужен и здесь, хотя манифест лаунчера — asInvoker: лаунчер
    /// штатно оказывается в elevated-процессе, когда пользователь запускает его
    /// «от имени администратора» (этот же сценарий уже учтён в
    /// <c>TrustedExecutablePaths</c> лаунчера). В таком запуске запись в
    /// %LocalAppData%\Ven4Tools — дерево, доступное на запись обычному процессу того
    /// же пользователя — становится тем же примитивом локального повышения
    /// привилегий: подменив каталог junction'ом, непривилегированный процесс
    /// перенаправит туда elevated-запись. Проверка идёт непосредственно перед
    /// записью, а не один раз при инициализации.</para>
    ///
    /// <para>Отказ здесь — исключение, а не тихий пропуск: вызывающие сохраняют
    /// пользовательские данные и уже обязаны переживать исключения записи.</para>
    /// </summary>
    private static void EnsureNotRedirected(string dir, string path)
    {
        if (IsReparsePoint(dir))
            throw new IOException($"Каталог подменён ссылкой, запись отменена: {dir}");
        if (IsReparsePoint(path))
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
}
