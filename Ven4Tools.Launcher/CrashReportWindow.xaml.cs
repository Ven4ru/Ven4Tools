using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    public partial class CrashReportWindow : Window
    {
        private readonly CrashReport _report;

        public CrashReportWindow(CrashReport report)
        {
            InitializeComponent();
            _report = report;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (DateTime.TryParse(_report.Timestamp, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            {
                txtSubtitle.Text = $"Версия {_report.Version}  ·  {dt.ToLocalTime():dd.MM.yyyy HH:mm:ss}";
                txtTime.Text     = dt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
            }

            txtVersion.Text = _report.Version;
            txtOs.Text      = _report.OsVersion;
            txtSession.Text = _report.SessionId;
            txtMachine.Text = _report.MachineName;
            txtExType.Text  = _report.ExceptionType;
            txtExMsg.Text   = _report.Message +
                              (_report.InnerMessage != null ? $"\n→ {_report.InnerMessage}" : "");
            txtStack.Text   = _report.StackTrace;
        }

        private async void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            btnCreate.IsEnabled = false;
            btnSkip.IsEnabled   = false;
            txtStatus.Text      = "⏳ Создаём issue на GitHub...";

            string title = $"[Crash] Ven4Tools {_report.Version} — {_report.ExceptionType.Split('.')[^1]}";
            string body  = BuildIssueBody();

            try
            {
                using var github = new GitHubService();
                var (ok, url, error) = await github.CreateIssueAsync(title, body);

                if (ok && url != null)
                {
                    MarkReported();
                    txtStatus.Text = $"✅ Issue создан";
                    btnCreate.Content   = "Открыть на GitHub";
                    btnCreate.IsEnabled = true;
                    btnCreate.Click    -= BtnCreate_Click;
                    btnCreate.Click    += (_, _) => { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); Close(); };
                    btnSkip.IsEnabled   = true;
                    btnSkip.Content     = "Закрыть";
                }
                else
                {
                    txtStatus.Text      = $"❌ {error}";
                    btnCreate.IsEnabled = true;
                    btnSkip.IsEnabled   = true;
                }
            }
            catch (Exception ex)
            {
                txtStatus.Text      = $"❌ {ex.Message}";
                btnCreate.IsEnabled = true;
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
                _report.Reported = true;
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Ven4Tools", "crash_last.json"),
                    JsonConvert.SerializeObject(_report, Formatting.Indented));
            }
            catch { }
        }

        private string BuildIssueBody()
        {
            string userNote = txtUserComment.Text.Trim();
            DateTime ts = DateTime.TryParse(_report.Timestamp, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var d)
                ? d.ToLocalTime() : DateTime.Now;

            // Issue уходит в публичный репозиторий — персональные данные не отправляем:
            // имя машины опускаем, SessionId заменяем коротким хэшем (хватает для
            // дедупликации), а текст исключения и stack trace очищаем от путей
            // профиля и имени пользователя.
            string sessionHash = GitHubService.HashSessionId(_report.SessionId);
            string exceptionBlock = GitHubService.SanitizePersonalData(
                $"{_report.ExceptionType}: {_report.Message}{(_report.InnerMessage != null ? $"\n  → {_report.InnerMessage}" : "")}");
            string stackTrace = GitHubService.SanitizePersonalData(_report.StackTrace);
            userNote = GitHubService.SanitizePersonalData(userNote);

            return $"""
## 🐛 Автоматический отчёт о вылете

| Поле | Значение |
|---|---|
| **Версия** | `{_report.Version}` |
| **Время** | {ts:dd.MM.yyyy HH:mm:ss} |
| **ОС** | {_report.OsVersion} |
| **Session** | `{sessionHash}` |

### Исключение

```
{exceptionBlock}
```

### Stack Trace

```
{stackTrace}
```

{(userNote.Length > 0 ? $"### Описание от пользователя\n\n{userNote}\n\n" : "")}
---
*Отчёт создан автоматически через Ven4Tools Launcher*
""";
        }
    }

    // Локальная копия модели чтобы не зависеть от клиентской сборки
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
