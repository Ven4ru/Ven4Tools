using System;
using System.IO;

namespace Ven4Tools.Services
{
    internal static class AppLogger
    {
        private const long MaxLogBytes = 1024 * 1024; // ~1 МБ, затем ротация

        private static readonly object _fileLock = new();
        private static string? _logPath;

        public static event Action<string>? MessageReceived;

        public static void Write(string message)
        {
            // Файловый лог: сообщения до подписки MainWindow (статические конструкторы,
            // Ранние сообщения сервисов раньше терялись безвозвратно
            WriteToFile(message);
            MessageReceived?.Invoke(message);
        }

        // Логирование исключения с контекстом: единый формат для catch-блоков
        public static void Write(Exception ex, string context)
        {
            Write($"{context}: {ex.Message}");
        }

        private static void WriteToFile(string message)
        {
            try
            {
                lock (_fileLock)
                {
                    if (_logPath == null)
                    {
                        string dir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Ven4Tools");
                        Directory.CreateDirectory(dir);
                        _logPath = Path.Combine(dir, "app.log");
                    }

                    // Простая ротация: при превышении лимита текущий лог становится app.old.log
                    var info = new FileInfo(_logPath);
                    if (info.Exists && info.Length > MaxLogBytes)
                    {
                        string oldPath = Path.Combine(info.DirectoryName!, "app.old.log");
                        try { File.Delete(oldPath); } catch { }
                        try { File.Move(_logPath, oldPath); } catch { }
                    }

                    File.AppendAllText(_logPath,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Логирование не должно ронять приложение
            }
        }
    }
}
