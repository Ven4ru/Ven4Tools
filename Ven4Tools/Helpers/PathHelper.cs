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
        => string.Concat(value.Select(c => InvalidFileNameChars.Contains(c) ? replacement : c));
}
