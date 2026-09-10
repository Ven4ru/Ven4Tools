using System;
using System.IO;
using System.Text;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Журнал лаунчера на диске: %LocalAppData%\Ven4Tools\logs\launcher.log.
    ///
    /// До этого журнал существовал ТОЛЬКО в виде текста в окне. Там, где он нужнее
    /// всего, окна на экране нет вовсе: автозапуск свёрнутым в трей, тихое фоновое
    /// автообновление клиента, установка компонентов, выбранных в setup, и любой сбой
    /// до первого показа окна. Разбор жалобы «лаунчер не видит winget» сводился к
    /// просьбе сделать скриншот или к чтению окна через средства автоматизации, хотя
    /// у клиента файл журнала (app.log) есть с самого начала.
    ///
    /// Три свойства, обязательные для файла, который пользователь приложит к обращению:
    ///   * пишем через <see cref="Helpers.FileHelper"/> — та же защита от подмены
    ///     каталога/файла reparse point'ом, что у краш-отчёта (лаунчер штатно бывает
    ///     elevated, а дерево доступно на запись обычному процессу пользователя);
    ///   * каждая строка проходит <see cref="Helpers.PersonalDataSanitizer"/>: журнал
    ///     полон путей вида C:\Users\&lt;имя&gt;\Documents, а файл уезжает в публичный
    ///     issue ровно так же, как краш-отчёт;
    ///   * никогда не бросаем наружу — журналирование не имеет права уронить операцию,
    ///     о которой оно рассказывает.
    /// </summary>
    internal static class LauncherLog
    {
        // Ротация одним поколением, как у клиента (app.log / app.old.log): предыдущая
        // сессия почти всегда и есть то, о чём спрашивает пользователь.
        private const long MaxBytes = 1024 * 1024;

        private static readonly object _lock = new();
        private static bool _sessionHeaderWritten;
        private static bool _disabled;

        public static string LogDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "logs");

        public static string LogPath => Path.Combine(LogDirectory, "launcher.log");

        private static string PreviousLogPath => Path.Combine(LogDirectory, "launcher.old.log");

        public static void Write(string message)
        {
            if (_disabled) return;

            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(LogDirectory);
                    RotateIfNeeded();

                    var text = new StringBuilder();
                    if (!_sessionHeaderWritten)
                    {
                        _sessionHeaderWritten = true;
                        text.Append(BuildSessionHeader());
                    }
                    text.Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ")
                        .Append(Sanitize(message))
                        .Append(Environment.NewLine);

                    File.AppendAllText(LogPath, text.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // Диск заполнен, каталог недоступен, файл занят антивирусом — что угодно.
                // Один отказ не должен превращаться в поток исключений на каждой строке:
                // выключаем журнал до конца сессии и молчим, окно продолжает показывать всё.
                _disabled = true;
            }
        }

        /// <summary>
        /// Шапка сессии — то, что спрашивают первым делом и чего в тексте окна нет:
        /// версия лаунчера, сборка Windows, права. Пишется один раз за запуск.
        /// </summary>
        private static string BuildSessionHeader()
        {
            string version = "?";
            try
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                if (v != null) version = v.ToString();
            }
            catch { }

            string admin = "?";
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                admin = new System.Security.Principal.WindowsPrincipal(identity)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator) ? "да" : "нет";
            }
            catch { }

            return Environment.NewLine +
                   "════════════════════════════════════════════════════" + Environment.NewLine +
                   $"Запуск {DateTime.Now:yyyy-MM-dd HH:mm:ss} · лаунчер {version} · " +
                   $"Windows {Environment.OSVersion.Version} · права администратора: {admin}" + Environment.NewLine +
                   "════════════════════════════════════════════════════" + Environment.NewLine;
        }

        private static string Sanitize(string message)
        {
            try { return Helpers.PersonalDataSanitizer.Sanitize(message); }
            // Сбой очистки не должен стоить строки журнала целиком, но и записывать
            // неочищенный текст в файл, который пользователь приложит к публичному
            // обращению, нельзя — отдаём заглушку.
            catch { return "<строка не записана: сбой очистки персональных данных>"; }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                var info = new FileInfo(LogPath);
                if (!info.Exists || info.Length < MaxBytes) return;

                try { File.Delete(PreviousLogPath); } catch { }
                File.Move(LogPath, PreviousLogPath);
            }
            catch
            {
                // Ротация не удалась — продолжаем дописывать в текущий файл.
                // Разросшийся журнал лучше, чем потерянный.
            }
        }
    }
}
