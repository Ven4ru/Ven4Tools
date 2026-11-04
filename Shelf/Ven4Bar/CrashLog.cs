using System;
using System.IO;

namespace Ven4Bar;

/// <summary>
/// Журнал ошибок %LOCALAPPDATA%\Ven4Bar\crash.log — по образцу Ven4Start: падения и проглоченные
/// ошибки, по которым иначе не понять, почему панель закрылась или не сохранила настройки.
/// Сам журнал не должен ронять панель, поэтому его ошибки глотаются.
/// </summary>
internal static class CrashLog
{
    private const long MaxBytes = 512 * 1024;   // больше — начинаем файл заново, чтобы журнал не рос бесконечно

    private static readonly object Lock = new();
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Bar", "crash.log");

    public static void Write(string what, Exception? ex = null)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes) File.WriteAllText(FilePath, "");
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {what}{(ex != null ? ": " + ex : "")}{Environment.NewLine}");
            }
        }
        catch { /* журнал не обязателен */ }
    }
}
