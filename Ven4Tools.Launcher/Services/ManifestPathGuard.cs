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
            // Хвостовые точку и пробел Windows отбрасывает при разборе пути: «a.dll.»
            // и «a.dll» — разные строки для планировщика (обе пройдут проверку на
            // дубликаты), но один файл на диске. Вторая запись набора перезаписала бы
            // резервную копию первой в InstallPartial, и откат потерял бы оригинал.
            // Настоящий файл публикации таким именем на Windows быть не может.
            if (segment.EndsWith('.') || segment.EndsWith(' ')) return false;
            // Короткое имя 8.3 («VEN4TO~1.DLL») — второе имя того же файла на диске:
            // та же лазейка мимо проверки дубликатов, что и хвостовая точка выше.
            // Генератор манифеста пишет только длинные имена публикации.
            if (LooksLikeShortName(segment)) return false;
            // Имена устройств DOS («NUL», «con.dll»): на Windows 10 такой путь ведёт
            // не в файл папки клиента, а на устройство.
            if (IsReservedDeviceName(segment)) return false;
        }

        return true;
    }

    private static readonly string[] ReservedDeviceNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static bool IsReservedDeviceName(string segment)
    {
        int dot = segment.IndexOf('.');
        string stem = (dot >= 0 ? segment[..dot] : segment).TrimEnd(' ');
        foreach (string name in ReservedDeviceNames)
        {
            if (string.Equals(stem, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // Форма короткого имени: до 8 символов основы с «~цифры» в конце и не больше
    // 3 символов расширения, одна точка.
    private static bool LooksLikeShortName(string segment)
    {
        int dot = segment.LastIndexOf('.');
        string stem = dot >= 0 ? segment[..dot] : segment;
        int extensionLength = dot >= 0 ? segment.Length - dot - 1 : 0;
        if (stem.Length == 0 || stem.Length > 8 || extensionLength > 3 || stem.Contains('.')) return false;

        int tilde = stem.LastIndexOf('~');
        if (tilde <= 0 || tilde == stem.Length - 1) return false;
        for (int i = tilde + 1; i < stem.Length; i++)
        {
            if (!char.IsAsciiDigit(stem[i])) return false;
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
