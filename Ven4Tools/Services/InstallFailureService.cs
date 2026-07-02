using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace Ven4Tools.Services
{
    public static class InstallFailureService
    {
        public static readonly string FailuresPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "failed_installs.json");

        private static readonly string _version =
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

        public static void Append(string appName, string appId, string method, string error)
        {
            try
            {
                var list = ReadAll();
                list.Add(new InstallFailure
                {
                    SessionId   = CrashReportService.SessionId,
                    // Случайный локальный идентификатор — не связан с именем машины
                    DeviceId    = CrashReportService.GetDeviceId(),
                    AppName     = appName,
                    AppId       = appId,
                    Method      = method,
                    Error       = error,
                    Version     = _version,
                    OsVersion   = Environment.OSVersion.ToString(),
                    Timestamp   = DateTime.UtcNow.ToString("O")
                });
                // Защита от неограниченного роста файла — храним не более 100 последних записей
                const int maxRecords = 100;
                if (list.Count > maxRecords)
                    list.RemoveRange(0, list.Count - maxRecords);
                Save(list);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "Ошибка сервиса сбоев установки");
            }
        }

        private static List<InstallFailure> ReadAll()
        {
            if (!File.Exists(FailuresPath)) return new();
            try { return JsonConvert.DeserializeObject<List<InstallFailure>>(
                File.ReadAllText(FailuresPath)) ?? new(); }
            catch { return new(); }
        }

        private static void Save(List<InstallFailure> list)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FailuresPath)!);
            File.WriteAllText(FailuresPath,
                JsonConvert.SerializeObject(list, Formatting.Indented));
        }
    }

    public class InstallFailure
    {
        public string SessionId   { get; set; } = "";
        public string DeviceId    { get; set; } = "";
        public string AppName     { get; set; } = "";
        public string AppId       { get; set; } = "";
        public string Method      { get; set; } = "";
        public string Error       { get; set; } = "";
        public string Version     { get; set; } = "";
        public string OsVersion   { get; set; } = "";
        public string Timestamp   { get; set; } = "";
    }
}
