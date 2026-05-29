using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Newtonsoft.Json;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    public partial class FeedbackReportWindow : Window
    {
        private readonly PendingFeedback _feedback;

        public FeedbackReportWindow(PendingFeedback feedback)
        {
            InitializeComponent();
            _feedback = feedback;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            txtTitle.Text       = $"Отзыв о Ven4Tools {_feedback.Version}";
            txtVersion.Text     = _feedback.Version;
            txtRating.Text      = new string('★', _feedback.Rating) +
                                  new string('☆', 5 - _feedback.Rating) +
                                  $"  ({_feedback.Rating}/5)";
            txtSession.Text     = _feedback.SessionId;
            txtMachine.Text     = _feedback.MachineName;
            txtFeedbackText.Text = string.IsNullOrWhiteSpace(_feedback.Text)
                ? "(пользователь не оставил комментарий)"
                : _feedback.Text;
        }

        private async void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            btnSend.IsEnabled = false;
            btnSkip.IsEnabled = false;
            txtStatus.Text    = "⏳ Отправка на GitHub...";

            string stars = new string('★', _feedback.Rating) + new string('☆', 5 - _feedback.Rating);
            DateTime ts  = DateTime.TryParse(_feedback.Timestamp, null,
                DateTimeStyles.RoundtripKind, out var d)
                ? d.ToLocalTime() : DateTime.Now;

            string title = $"[Feedback] Ven4Tools {_feedback.Version} — {stars} ({_feedback.Rating}/5)";
            string body  = $"""
## 💬 Pre-release Feedback — Ven4Tools {_feedback.Version}

| Поле | Значение |
|---|---|
| **Версия** | `{_feedback.Version}` |
| **Оценка** | {stars} ({_feedback.Rating}/5) |
| **Время** | {ts:dd.MM.yyyy HH:mm} |
| **Машина** | `{_feedback.MachineName}` |
| **Session ID** | `{_feedback.SessionId}` |

### Отзыв пользователя

{(string.IsNullOrWhiteSpace(_feedback.Text) ? "*Пользователь не оставил комментарий*" : _feedback.Text)}

---
*Автоматически отправлено через Ven4Tools Launcher*
""";

            try
            {
                using var github = new GitHubService();
                var (ok, url, error) = await github.CreateIssueAsync(
                    title, body, new[] { "feedback", "pre-release" });

                if (ok && url != null)
                {
                    MarkReported();
                    txtStatus.Text      = "✅ Отзыв отправлен";
                    btnSend.Content     = "Открыть на GitHub";
                    btnSend.IsEnabled   = true;
                    btnSend.Click      -= BtnSend_Click;
                    btnSend.Click      += (_, _) =>
                    {
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                        Close();
                    };
                    btnSkip.Content     = "Закрыть";
                    btnSkip.IsEnabled   = true;
                }
                else
                {
                    txtStatus.Text      = $"❌ {error}";
                    btnSend.IsEnabled   = true;
                    btnSkip.IsEnabled   = true;
                }
            }
            catch (Exception ex)
            {
                txtStatus.Text      = $"❌ {ex.Message}";
                btnSend.IsEnabled   = true;
                btnSkip.IsEnabled   = true;
            }
        }

        private void BtnSkip_Click(object sender, RoutedEventArgs e)
        {
            MarkReported();
            Close();
        }

        private void MarkReported()
        {
            try
            {
                _feedback.Reported = true;
                System.IO.File.WriteAllText(PendingFeedback.FeedbackPath,
                    JsonConvert.SerializeObject(_feedback, Formatting.Indented));
            }
            catch { }
        }
    }

    public class PendingFeedback
    {
        public static readonly string FeedbackPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "pending_feedback.json");

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
