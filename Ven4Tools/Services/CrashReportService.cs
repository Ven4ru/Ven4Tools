using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Ven4Tools.Services
{
    public static class CrashReportService
    {
        public static readonly string CrashFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "crash_last.json");

        // Генерируется один раз при старте приложения
        public static readonly string SessionId =
            Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

        // Единый переиспользуемый HttpClient на уровне класса, как во всех прочих
        // сервисах проекта: пересоздание клиента на каждый вызов исчерпывает сокеты.
        private static readonly System.Net.Http.HttpClient _http =
            new() { Timeout = TimeSpan.FromSeconds(10) };

        public static void Write(Exception ex)
        {
            try
            {
                var report = new CrashReport
                {
                    SessionId     = SessionId,
                    Version       = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown",
                    // Точность до секунд (не до 100 нс, как формат "O") — чтобы метка
                    // времени не служила дополнительным fingerprint-вектором. Формат
                    // остаётся валидным ISO 8601 с суффиксом Z и парсится стандартно.
                    Timestamp     = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'"),
                    OsVersion     = Environment.OSVersion.ToString(),
                    ExceptionType = ex.GetType().FullName ?? ex.GetType().Name,
                    Message       = SanitizePath(ex.Message),
                    StackTrace    = SanitizePath(ex.StackTrace ?? ""),
                    InnerMessage  = ex.InnerException != null ? SanitizePath(ex.InnerException.Message) : null,
                    Reported      = false,
                    SendApproved  = false
                };

                Directory.CreateDirectory(Path.GetDirectoryName(CrashFilePath)!);
                File.WriteAllText(CrashFilePath, JsonConvert.SerializeObject(report, Formatting.Indented));
            }
            catch (Exception logEx) { AppLogger.Write($"[CrashReportService] {logEx.Message}"); }
        }

        public static CrashReport? Read()
        {
            try
            {
                if (!File.Exists(CrashFilePath)) return null;
                var json = File.ReadAllText(CrashFilePath);
                return JsonConvert.DeserializeObject<CrashReport>(json);
            }
            catch (Exception ex) { AppLogger.Write($"[CrashReportService] {ex.Message}"); return null; }
        }

        /// <summary>
        /// Помечает отложенный отчёт как одобренный пользователем к отправке.
        /// Если отправка сорвётся (нет сети), при следующем старте отчёт уйдёт
        /// без повторного вопроса — согласие уже получено.
        /// </summary>
        public static void MarkSendApproved()
        {
            try
            {
                var report = Read();
                if (report == null) return;
                report.SendApproved = true;
                File.WriteAllText(CrashFilePath, JsonConvert.SerializeObject(report, Formatting.Indented));
            }
            catch (Exception ex) { AppLogger.Write($"[CrashReportService] {ex.Message}"); }
        }

        /// <summary>
        /// Удаляет файл отложенного отчёта о сбое. Вызывается в двух случаях:
        /// пользователь отказался от отправки либо отчёт успешно отправлен —
        /// в обоих локальный файл больше не нужен и не должен оставаться на диске.
        /// </summary>
        public static void DeletePending()
        {
            try
            {
                if (File.Exists(CrashFilePath)) File.Delete(CrashFilePath);
            }
            catch (Exception ex) { AppLogger.Write($"[CrashReportService] {ex.Message}"); }
        }

        /// <summary>
        /// Отправляет отложенный краш-репорт на сервер (best-effort).
        /// Вызывается при старте приложения ТОЛЬКО после явного согласия
        /// пользователя (SendApproved) — без него метод ничего не отправляет.
        /// </summary>
        public static async Task TrySendPendingAsync()
        {
            // Параноидальный режим: отправка краш-отчётов на сервер запрещена.
            if (ProfileService.Current.ParanoidMode) return;
            try
            {
                var report = Read();
                if (report == null || report.Reported || !report.SendApproved) return;

                var payload = new System.Net.Http.FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("action",     "crash_report"),
                    new KeyValuePair<string, string>("session_id", report.SessionId),
                    new KeyValuePair<string, string>("version",    report.Version),
                    new KeyValuePair<string, string>("timestamp",  report.Timestamp),
                    new KeyValuePair<string, string>("os",         report.OsVersion),
                    new KeyValuePair<string, string>("type",       report.ExceptionType),
                    new KeyValuePair<string, string>("message",    report.Message),
                    new KeyValuePair<string, string>("trace",      report.StackTrace ?? ""),
                });

                var response = await _http.PostAsync(ApiConfig.DbApi, payload);

                // Сервер всегда отвечает HTTP 200 — даже при ошибке или
                // несуществующем action. Поэтому проверяем не статус,
                // а тело ответа на признак успеха ("success": true).
                if (!response.IsSuccessStatusCode) return;

                var bodyText = await response.Content.ReadAsStringAsync();
                if (IsSuccessBody(bodyText))
                    DeletePending();
            }
            catch (Exception ex) { AppLogger.Write($"[CrashReportService] TrySendPending: {ex.Message}"); }
        }

        /// <summary>
        /// Проверяет тело ответа сервера на признак успеха.
        /// db.php при успехе возвращает {"success": true}, при ошибке —
        /// {"error": "..."}. HTTP-статус всегда 200, поэтому опираемся
        /// именно на содержимое тела.
        /// </summary>
        internal static bool IsSuccessBody(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;
            try
            {
                var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
                if (obj == null) return false;
                // Явная ошибка — не успех.
                if (obj.ContainsKey("error")) return false;
                // Признак успеха.
                return obj.TryGetValue("success", out var s)
                       && s != null
                       && string.Equals(s.ToString(), "true", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Обезличивает реальные пути пользователя в тексте отчёта:
        /// C:\Users\имя\... превращается в %USERPROFILE%\... и т.д.
        /// Сначала заменяем более глубокие пути (LocalAppData/AppData),
        /// затем профиль целиком — иначе вложенные пути не совпадут.
        /// </summary>
        internal static string SanitizePath(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localApp))
                text = text.Replace(localApp, "%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase);

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
                text = text.Replace(appData, "%APPDATA%", StringComparison.OrdinalIgnoreCase);

            var temp = Path.GetTempPath().TrimEnd('\\');
            if (!string.IsNullOrEmpty(temp))
                text = text.Replace(temp, "%TEMP%", StringComparison.OrdinalIgnoreCase);

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
                text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

            // Пути ЧУЖОГО профиля (например, в исключении фигурирует C:\Users\ИмяДругогоПользователя\...,
            // а не текущего) — замены выше это не ловят, так как завязаны на Environment.UserName/*Profile
            // текущего пользователя. Тот же regex, что в GitHubService.SanitizePersonalData (лаунчер).
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"([A-Za-z]:\\Users\\)[^\\\r\n]+",
                "$1<user>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Тот же путь, но с forward-slash (встречается в URI/некоторых стектрейсах
            // сторонних библиотек) и UNC-путь без буквы диска (\\server\Users\Имя\...).
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"([A-Za-z]:/Users/)[^/\r\n]+",
                "$1<user>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"(\\\\[^\\\r\n]+\\Users\\)[^\\\r\n]+",
                "$1<user>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Убираем имя пользователя и имя машины из непутевого контекста (SQL-ошибки, UNC-пути).
            // Делаем это в конце — после замены путей на переменные окружения,
            // иначе имя внутри путей пропало бы и %LOCALAPPDATA%/%USERPROFILE% не сработали.
            // Короткие значения (< 3 символов) не заменяем — слишком много ложных срабатываний.
            var userName = Environment.UserName;
            if (!string.IsNullOrEmpty(userName) && userName.Length >= 3)
                text = text.Replace(userName, "<user>", StringComparison.OrdinalIgnoreCase);

            var machineName = Environment.MachineName;
            if (!string.IsNullOrEmpty(machineName) && machineName.Length >= 3)
                text = text.Replace(machineName, "<machine>", StringComparison.OrdinalIgnoreCase);

            return text;
        }

    }

    public class CrashReport
    {
        public string  SessionId     { get; set; } = "";
        public string  Version       { get; set; } = "";
        public string  Timestamp     { get; set; } = "";
        public string  OsVersion     { get; set; } = "";
        public string  ExceptionType { get; set; } = "";
        public string  Message       { get; set; } = "";
        public string  StackTrace    { get; set; } = "";
        public string? InnerMessage  { get; set; }
        public bool    Reported      { get; set; }
        // Явное согласие пользователя на отправку этого отчёта
        public bool    SendApproved  { get; set; }
    }
}
