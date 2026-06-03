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
                    MachineName   = Environment.MachineName,
                    Version       = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown",
                    Timestamp     = DateTime.UtcNow.ToString("O"),
                    OsVersion     = Environment.OSVersion.ToString(),
                    ExceptionType = ex.GetType().FullName ?? ex.GetType().Name,
                    Message       = ex.Message,
                    StackTrace    = ex.StackTrace ?? "",
                    InnerMessage  = ex.InnerException?.Message,
                    Reported      = false
                };

                Directory.CreateDirectory(Path.GetDirectoryName(CrashFilePath)!);
                File.WriteAllText(CrashFilePath, JsonConvert.SerializeObject(report, Formatting.Indented));
            }
            catch { }
        }

        public static CrashReport? Read()
        {
            try
            {
                if (!File.Exists(CrashFilePath)) return null;
                var json = File.ReadAllText(CrashFilePath);
                return JsonConvert.DeserializeObject<CrashReport>(json);
            }
            catch { return null; }
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
            catch { }
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
