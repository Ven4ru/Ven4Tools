using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Ven4Tools.Helpers;

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
                    AppName     = appName,
                    AppId       = appId,
                    Method      = method,
                    Error       = error,
                    Version     = _version,
                    OsVersion   = Environment.OSVersion.ToString(),
                    Timestamp   = DateTime.UtcNow.ToString("O"),
                    // При включённом параноидальном режиме запись сразу помечается
                    // «уже отчитались»: единственный потребитель этого флага — лаунчер,
                    // который иначе предложил бы опубликовать её ПУБЛИЧНЫМ issue на
                    // GitHub при первом же запуске с выключенным режимом. Клиент флаг
                    // не смотрит, поэтому список «Повторить» и панель неудачных
                    // установок работают как раньше.
                    Reported    = ProfileService.Current.ParanoidMode
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

        /// <summary>
        /// Все записи журнала сбоев (от старых к новым). Читает клиент — чтобы
        /// показать пользователю его собственные неудачные установки; тот же файл
        /// независимо читает лаунчер для отчёта автору, поэтому формат на диске
        /// менять нельзя.
        /// </summary>
        public static List<InstallFailure> ReadAll()
        {
            if (!File.Exists(FailuresPath)) return new();
            try { return JsonConvert.DeserializeObject<List<InstallFailure>>(
                File.ReadAllText(FailuresPath)) ?? new(); }
            catch { return new(); }
        }

        private static void Save(List<InstallFailure> list)
        {
            FileHelper.WriteAllTextAtomic(FailuresPath,
                JsonConvert.SerializeObject(list, Formatting.Indented));
        }
    }

    public class InstallFailure
    {
        public string SessionId   { get; set; } = "";
        public string AppName     { get; set; } = "";
        public string AppId       { get; set; } = "";
        public string Method      { get; set; } = "";
        public string Error       { get; set; } = "";
        public string Version     { get; set; } = "";
        public string OsVersion   { get; set; } = "";
        public string Timestamp   { get; set; } = "";

        // Пишется и читается только лаунчером (InstallReportWindow) после отправки
        // отчёта. Клиент это поле никогда не выставляет — но обязан объявить его
        // здесь: Append перечитывает файл целиком через этот тип и пишет обратно,
        // и без свойства Reported флаг «уже отчитались» терялся бы у всех прошлых
        // записей при каждом новом сбое, заставляя лаунчер заново предлагать
        // отправить уже отправленные отчёты.
        public bool Reported { get; set; }
    }
}
