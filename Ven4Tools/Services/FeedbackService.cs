using System;
using System.IO;
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
                var payload = new
                {
                    SessionId   = CrashReportService.SessionId,
                    MachineName = Environment.MachineName,
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
            catch { }
        }
    }
}
