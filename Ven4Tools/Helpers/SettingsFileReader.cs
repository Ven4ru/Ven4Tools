using System;
using System.IO;
using System.Text;
using System.Threading;
using Ven4Tools.Services;

namespace Ven4Tools.Helpers;

/// <summary>Итог чтения файла настроек.</summary>
internal enum SettingsReadResult
{
    /// <summary>Файла нет — настроек ещё не было, писать можно.</summary>
    Missing,
    /// <summary>Файл прочитан.</summary>
    Read,
    /// <summary>Файл есть, но не читается (занят, нет доступа) — писать поверх нельзя.</summary>
    Unreadable
}

/// <summary>
/// Чтение файла настроек, который держится в памяти весь сеанс и целиком
/// перезаписывается при каждом сохранении (profile.json, favorites.json,
/// hidden.json, apps.json).
///
/// Раньше любая ошибка чтения при старте оставляла в памяти значения по умолчанию,
/// и первое же сохранение записывало их поверх файла: достаточно, чтобы файл на
/// мгновение был занят антивирусом или облачной синхронизацией, — и тема, пины,
/// избранное или добавленные вручную приложения молча терялись. Тот же класс
/// ошибки, от которого <see cref="JsonListFile"/> защищает историю и пресеты, но
/// для хранилищ «прочитал один раз при старте».
///
/// Здесь две меры: короткие повторы чтения (блокировка почти всегда мгновенная) и
/// явный итог <see cref="SettingsReadResult.Unreadable"/>, по которому хранилище
/// запрещает запись, пока файл не удастся прочитать.
/// </summary>
internal static class SettingsFileReader
{
    private const int Attempts = 3;

    public static SettingsReadResult TryRead(string path, string logPrefix, out string text)
    {
        text = string.Empty;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return SettingsReadResult.Missing;
                text = File.ReadAllText(path, Encoding.UTF8);
                return SettingsReadResult.Read;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= Attempts)
                {
                    AppLogger.Write($"{logPrefix} Файл не прочитан, запись отложена, чтобы не затереть данные: {ex.Message}");
                    return SettingsReadResult.Unreadable;
                }
                Thread.Sleep(100 * attempt);
            }
        }
    }

    /// <summary>
    /// Откладывает повреждённый файл рядом с суффиксом .corrupt-ГГГГММДДччммсс, чтобы
    /// следующая запись значений по умолчанию не уничтожила его безвозвратно.
    /// false — отложить не удалось, писать поверх нельзя.
    /// </summary>
    public static bool SetAsideCorrupt(string path, string logPrefix, string reason)
    {
        string backup = $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
        try
        {
            // Elevated-процесс переименовывает файл в пользовательском дереве —
            // тот же guard от подмены reparse point'ом, что и у записи.
            if (PathHelper.IsReparsePoint(Path.GetDirectoryName(path)!) || PathHelper.IsReparsePoint(path))
                return false;
            File.Move(path, backup);
            AppLogger.Write($"{logPrefix} Файл повреждён ({reason}) — отложен в {Path.GetFileName(backup)}, используются значения по умолчанию");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Write($"{logPrefix} Файл повреждён и не отложен ({ex.Message}) — запись отложена");
            return false;
        }
    }
}
