using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ven4Tools.Helpers;

/// <summary>
/// Замена недопустимых для имени файла символов — общая часть, которая была
/// независимо реализована в ConfigSnapshotService/OfflineService/InstallationService
/// тремя чуть разными способами (в т.ч. один из них удалял символы вместо замены,
/// создавая риск коллизии двух разных исходных строк в одно имя файла).
/// </summary>
internal static class PathHelper
{
    private static readonly HashSet<char> InvalidFileNameChars = new(Path.GetInvalidFileNameChars());

    public static string SanitizeFileNameComponent(string value, char replacement = '_')
    {
        string result = string.Concat(value.Select(c => InvalidFileNameChars.Contains(c) ? replacement : c));
        // «.» и «..» состоят из допустимых символов, но компонентом имени не являются:
        // Path.Combine(dir, "..") выводит на уровень выше (INSTALLDIR="…\Program Files\.."
        // из названия, введённого при добавлении локального установщика).
        if (result.Length > 0 && result.Trim().All(c => c == '.'))
            result = new string(replacement, result.Length);
        return result;
    }

    /// <summary>
    /// Является ли путь reparse point (junction/symlink).
    ///
    /// Клиент работает elevated, а %LocalAppData%\Ven4Tools доступен на запись
    /// непривилегированному процессу того же пользователя — тот может подменить
    /// каталог или файл junction'ом на защищённую цель, и тогда elevated-запись
    /// уйдёт туда, куда перенаправляет подмена. Поэтому каждый путь, в который
    /// пишет elevated-процесс под пользовательским каталогом, проверяется
    /// непосредственно перед записью (не единожды при инициализации — иначе
    /// подмену можно было бы сделать уже после проверки).
    ///
    /// Не удалось определить — возвращаем true (fail-closed, вызывающий не пишет).
    /// </summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch { return true; }
    }
}
