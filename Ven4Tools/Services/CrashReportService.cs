using System;
using System.IO;
using System.Reflection;
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
