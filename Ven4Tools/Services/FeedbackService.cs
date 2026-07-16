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

        // Единый переиспользуемый HttpClient на уровне класса, как во всех прочих
        // сервисах проекта: пересоздание клиента на каждый вызов исчерпывает сокеты.
        private static readonly System.Net.Http.HttpClient _http =
            new() { Timeout = TimeSpan.FromSeconds(10) };

        public static void Write(int rating, string text)
        {
            try
            {
                var payload = new FeedbackRecord
                {
                    SessionId   = CrashReportService.SessionId,
                    Version     = ChannelService.InstalledVersion,
                    Channel     = "prerelease",
                    Rating      = rating,
                    Text        = CrashReportService.SanitizePath(text),
                    // Точность до секунд (не до 100 нс) — метка времени не должна служить
                    // дополнительным fingerprint-вектором. Валидный ISO 8601 с суффиксом Z.
                    Timestamp   = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'"),
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

        /// <summary>
        /// Удаляет файл отложенного отзыва после успешной отправки: локальный файл
        /// больше не нужен и не должен оставаться на диске.
        /// </summary>
        public static void DeletePending()
        {
            try
            {
                if (File.Exists(FeedbackPath)) File.Delete(FeedbackPath);
            }
            catch (Exception ex) { AppLogger.Write($"[FeedbackService] {ex.Message}"); }
        }

        /// <summary>
        /// Отправляет неотправленный отзыв на сервер (best-effort).
        /// Вызывается при старте приложения — если отзыв был записан ранее,
        /// он уйдёт сейчас, а локальный файл после успешной отправки удаляется.
        /// </summary>
        public static async Task TrySendPendingAsync()
        {
            // Параноидальный режим: отправка отзывов на сервер запрещена.
            if (ProfileService.Current.ParanoidMode) return;
            try
            {
                var record = Read();
                if (record == null || record.Reported) return;

                var payload = new System.Net.Http.FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("action",     "submit_feedback"),
                    new KeyValuePair<string, string>("session_id", record.SessionId),
                    new KeyValuePair<string, string>("version",    record.Version),
                    new KeyValuePair<string, string>("channel",    record.Channel),
                    new KeyValuePair<string, string>("rating",     record.Rating.ToString()),
                    new KeyValuePair<string, string>("text",       record.Text),
                    new KeyValuePair<string, string>("timestamp",  record.Timestamp),
                });

                var response = await _http.PostAsync(ApiConfig.DbApi, payload);

                // Сервер всегда отвечает HTTP 200 — успех определяем по телу ответа
                if (!response.IsSuccessStatusCode) return;

                var bodyText = await response.Content.ReadAsStringAsync();
                if (CrashReportService.IsSuccessBody(bodyText))
                    DeletePending();
            }
            catch (Exception ex) { AppLogger.Write($"[FeedbackService] TrySendPending: {ex.Message}"); }
        }
    }

    public class FeedbackRecord
    {
        public string SessionId   { get; set; } = "";
        public string Version     { get; set; } = "";
        public string Channel     { get; set; } = "";
        public int    Rating      { get; set; }
        public string Text        { get; set; } = "";
        public string Timestamp   { get; set; } = "";
        public bool   Reported    { get; set; }
    }
}
