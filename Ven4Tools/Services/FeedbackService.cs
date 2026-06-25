using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Ven4Tools.Services
{
    public static class FeedbackService
    {
        public static readonly string FeedbackPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "pending_feedback.json");

        public static void Write(int rating, string text)
        {
            try
            {
                var payload = new FeedbackRecord
                {
                    SessionId   = CrashReportService.SessionId,
                    // Имя машины не сохраняем в открытом виде — только короткий хеш
                    MachineName = CrashReportService.AnonymizeMachineName(),
                    Version     = ChannelService.InstalledVersion,
                    Channel     = "prerelease",
                    Rating      = rating,
                    Text        = text,
                    Timestamp   = DateTime.UtcNow.ToString("O"),
                    Reported    = false
                };
                Directory.CreateDirectory(Path.GetDirectoryName(FeedbackPath)!);
                File.WriteAllText(FeedbackPath,
                    JsonConvert.SerializeObject(payload, Formatting.Indented));
            }
            catch (Exception ex) { AppLogger.Write($"[FeedbackService] Сохранение отзыва: {ex.Message}"); }
        }

        public static FeedbackRecord? Read()
        {
            try
            {
                if (!File.Exists(FeedbackPath)) return null;
                return JsonConvert.DeserializeObject<FeedbackRecord>(File.ReadAllText(FeedbackPath));
            }
            catch (Exception ex) { AppLogger.Write($"[FeedbackService] {ex.Message}"); return null; }
        }

        public static void MarkReported()
        {
            try
            {
                var record = Read();
                if (record == null) return;
                record.Reported = true;
                File.WriteAllText(FeedbackPath, JsonConvert.SerializeObject(record, Formatting.Indented));
            }
            catch (Exception ex) { AppLogger.Write($"[FeedbackService] {ex.Message}"); }
        }

        /// <summary>
        /// Отправляет неотправленный отзыв на сервер (best-effort).
        /// Вызывается при старте приложения — если отзыв был записан ранее,
        /// он уйдёт сейчас и будет помечен как отправленный.
        /// </summary>
        public static async Task TrySendPendingAsync()
        {
            try
            {
                var record = Read();
                if (record == null || record.Reported) return;

                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var payload = new System.Net.Http.FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("action",     "submit_feedback"),
                    new KeyValuePair<string, string>("session_id", record.SessionId),
                    new KeyValuePair<string, string>("machine",    record.MachineName),
                    new KeyValuePair<string, string>("version",    record.Version),
                    new KeyValuePair<string, string>("channel",    record.Channel),
                    new KeyValuePair<string, string>("rating",     record.Rating.ToString()),
                    new KeyValuePair<string, string>("text",       record.Text),
                    new KeyValuePair<string, string>("timestamp",  record.Timestamp),
                });

                var response = await http.PostAsync(ApiConfig.DbApi, payload);

                // Сервер всегда отвечает HTTP 200 — успех определяем по телу ответа
                if (!response.IsSuccessStatusCode) return;

                var bodyText = await response.Content.ReadAsStringAsync();
                if (CrashReportService.IsSuccessBody(bodyText))
                    MarkReported();
            }
            catch (Exception ex) { AppLogger.Write($"[FeedbackService] TrySendPending: {ex.Message}"); }
        }
    }

    public class FeedbackRecord
    {
        public string SessionId   { get; set; } = "";
        public string MachineName { get; set; } = "";
        public string Version     { get; set; } = "";
        public string Channel     { get; set; } = "";
        public int    Rating      { get; set; }
        public string Text        { get; set; } = "";
        public string Timestamp   { get; set; } = "";
        public bool   Reported    { get; set; }
    }
}
