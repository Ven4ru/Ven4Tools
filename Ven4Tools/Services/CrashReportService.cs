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

        public static void Write(Exception ex)
        {
            try
            {
                var report = new CrashReport
                {
                    SessionId     = SessionId,
                    // Имя машины не отправляем в открытом виде — только короткий хеш
                    MachineName   = AnonymizeMachineName(),
                    Version       = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown",
                    Timestamp     = DateTime.UtcNow.ToString("O"),
                    OsVersion     = Environment.OSVersion.ToString(),
                    ExceptionType = ex.GetType().FullName ?? ex.GetType().Name,
                    Message       = SanitizePath(ex.Message),
                    StackTrace    = SanitizePath(ex.StackTrace ?? ""),
                    InnerMessage  = ex.InnerException != null ? SanitizePath(ex.InnerException.Message) : null,
                    Reported      = false
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

        public static void MarkReported()
        {
            try
            {
                var report = Read();
                if (report == null) return;
                report.Reported = true;
                File.WriteAllText(CrashFilePath, JsonConvert.SerializeObject(report, Formatting.Indented));
            }
            catch (Exception ex) { AppLogger.Write($"[CrashReportService] {ex.Message}"); }
        }

        /// <summary>
        /// Отправляет неотправленный краш-репорт на сервер (best-effort).
        /// Вызывается при старте приложения — если прошлый сеанс упал,
        /// отчёт уйдёт сейчас и будет помечен как отправленный.
        /// </summary>
        public static async Task TrySendPendingAsync()
        {
            try
            {
                var report = Read();
                if (report == null || report.Reported) return;

                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var payload = new System.Net.Http.FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("action",     "crash_report"),
                    new KeyValuePair<string, string>("session_id", report.SessionId),
                    new KeyValuePair<string, string>("machine",    report.MachineName),
                    new KeyValuePair<string, string>("version",    report.Version),
                    new KeyValuePair<string, string>("timestamp",  report.Timestamp),
                    new KeyValuePair<string, string>("os",         report.OsVersion),
                    new KeyValuePair<string, string>("type",       report.ExceptionType),
                    new KeyValuePair<string, string>("message",    report.Message),
                    new KeyValuePair<string, string>("trace",      report.StackTrace ?? ""),
                });

                var response = await http.PostAsync(ApiConfig.DbApi, payload);

                // Сервер всегда отвечает HTTP 200 — даже при ошибке или
                // несуществующем action. Поэтому проверяем не статус,
                // а тело ответа на признак успеха ("success": true).
                if (!response.IsSuccessStatusCode) return;

                var bodyText = await response.Content.ReadAsStringAsync();
                if (IsSuccessBody(bodyText))
                    MarkReported();
            }
            catch (Exception ex) { AppLogger.Write($"[CrashReportService] TrySendPending: {ex.Message}"); }
        }

        /// <summary>
        /// Проверяет тело ответа сервера на признак успеха.
        /// db.php при успехе возвращает {"success": true}, при ошибке —
        /// {"error": "..."}. HTTP-статус всегда 200, поэтому опираемся
        /// именно на содержимое тела.
        /// </summary>
        private static bool IsSuccessBody(string? body)
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
        private static string SanitizePath(string text)
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

            return text;
        }

        /// <summary>
        /// Короткий SHA256-хеш имени машины: позволяет группировать отчёты
        /// с одного ПК, не раскрывая реальное имя компьютера.
        /// </summary>
        private static string AnonymizeMachineName()
        {
            try
            {
                var bytes = System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(Environment.MachineName));
                return Convert.ToHexString(bytes)[..8];
            }
            catch { return "unknown"; }
        }
    }

    public class CrashReport
    {
        public string  SessionId     { get; set; } = "";
        public string  MachineName   { get; set; } = "";
        public string  Version       { get; set; } = "";
        public string  Timestamp     { get; set; } = "";
        public string  OsVersion     { get; set; } = "";
        public string  ExceptionType { get; set; } = "";
        public string  Message       { get; set; } = "";
        public string  StackTrace    { get; set; } = "";
        public string? InnerMessage  { get; set; }
        public bool    Reported      { get; set; }
    }
}
