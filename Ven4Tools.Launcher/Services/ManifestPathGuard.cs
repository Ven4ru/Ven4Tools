using System;
using System.IO;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Проверка относительных путей из файлового манифеста клиента перед тем, как
/// превращать их в реальные пути внутри папки установленного клиента.
///
/// Манифест подписан, поэтому в штатном сценарии путь заведомо корректен. Но
/// именно это место превращает СТРОКУ ИЗ СЕТИ в путь на диске, поэтому проверка
/// обязана быть здесь и без оглядки на подпись: тот же класс защиты, что
/// zip-slip в <see cref="SafeZipExtractor"/> — один слой контроля целостности
/// не отменяет второго. Компрометация ключа подписи не должна давать записи
/// «..\..\Windows\System32\...» вместо файла публикации.
///
/// Чистые функции без диска — покрыты unit-тестами.
/// </summary>
internal static class ManifestPathGuard
{
    /// <summary>
    /// Путь пригоден для использования: непустой, относительный, только с '/'
    /// как разделителем (формат манифеста), без выхода вверх по дереву, без
    /// корня/буквы диска и без обратных слэшей (они на Windows тоже разделитель,
    /// а значит через них можно было бы протащить обход).
    /// </summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.IndexOf('\\') >= 0) return false;
        if (path.StartsWith('/')) return false;
        if (path.Contains(':')) return false;
        if (Path.IsPathRooted(path)) return false;
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;

        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0) return false;      // «a//b» или хвостовой слэш
            if (segment == "." || segment == "..") return false;
        }

        return true;
    }

    /// <summary>
    /// Полный путь файла внутри каталога клиента. Бросает исключение, если путь
    /// небезопасен или после нормализации оказался вне <paramref name="rootFullPath"/>
    /// (двойная проверка: формальная по строке и фактическая по результату
    /// нормализации — на случай экзотики вроде хвостовых точек/пробелов, которые
    /// Windows отбрасывает при разборе пути).
    /// </summary>
    public static string ResolveInside(string rootFullPath, string relativePath)
    {
        if (!IsSafeRelativePath(relativePath))
        {
            throw new InvalidOperationException($"Недопустимый путь в манифесте: {relativePath}");
        }

        string root = Path.GetFullPath(rootFullPath);
        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        string full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Путь из манифеста выходит за пределы папки клиента: {relativePath}");
        }

        return full;
    }
}
