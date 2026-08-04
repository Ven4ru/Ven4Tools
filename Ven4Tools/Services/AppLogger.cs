using System;
using System.IO;
using Ven4Tools.Helpers;

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

        /// <summary>
        /// Удаляет файлы общего журнала приложения (app.log и ротированный app.old.log).
        /// Нужен кнопке «Очистить логи» на вкладке «Диагностика»: она удаляла только
        /// журналы установок в подпапке logs, а общий журнал лежит уровнем выше — и
        /// переживал очистку, хотя пользователю обещано удалить ВСЕ файлы логов.
        /// Удаление идёт под тем же замком, что и запись, чтобы не пересечься с ней.
        /// </summary>
        public static void ClearAppLogFiles()
        {
            lock (_fileLock)
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Ven4Tools");
                foreach (var name in new[] { "app.log", "app.old.log" })
                {
                    try { File.Delete(Path.Combine(dir, name)); } catch { }
                }
            }
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

                    // Пишем/двигаем файл только если ни каталог, ни сам файл лога не
                    // являются reparse point — обоснование см. в PathHelper.IsReparsePoint
                    // (тот же guard применён к журналу установки в InstallationService).
                    if (PathHelper.IsReparsePoint(Path.GetDirectoryName(_logPath)!) ||
                        PathHelper.IsReparsePoint(_logPath))
                        return;

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
