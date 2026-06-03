using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    public partial class InstallReportWindow : Window
    {
        private readonly List<InstallFailure> _failures;

        public InstallReportWindow(List<InstallFailure> failures)
        {
            InitializeComponent();
            _failures = failures;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            txtTitle.Text = $"{_failures.Count} приложений не удалось установить";

            foreach (var f in _failures)
            {
                DateTime ts = DateTime.TryParse(f.Timestamp, null,
                    DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime() : DateTime.Now;

                var card = new Border
                {
                    Background       = new SolidColorBrush(Color.FromRgb(0x16, 0x1b, 0x22)),
                    BorderBrush      = new SolidColorBrush(Color.FromRgb(0xe3, 0x6e, 0x09)),
                    BorderThickness  = new Thickness(1),
                    CornerRadius     = new CornerRadius(8),
                    Padding          = new Thickness(14, 10, 14, 10),
                    Margin           = new Thickness(0, 0, 0, 8)
                };

                var sp = new StackPanel();

                sp.Children.Add(MakeRow("📦 Приложение", f.AppName, "#e6edf3", bold: true));
                sp.Children.Add(MakeRow("🆔 ID",         f.AppId,   "#8b949e"));
                sp.Children.Add(MakeRow("⚙ Метод",       f.Method,  "#8b949e"));
                sp.Children.Add(MakeRow("❌ Ошибка",      f.Error,   "#f85149"));
                sp.Children.Add(MakeRow("🕐 Время",   ts.ToString("dd.MM.yyyy HH:mm"), "#8b949e"));
                sp.Children.Add(MakeRow("🔑 Сессия",      f.SessionId, "#58a6ff"));

                card.Child = sp;
                pnlFailures.Children.Add(card);
            }
        }

        private static UIElement MakeRow(string label, string value,
            string color, bool bold = false)
        {
            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 2, 0, 2)
            };
            sp.Children.Add(new TextBlock
            {
                Text       = label + ": ",
                Foreground = new SolidColorBrush(Color.FromRgb(0x8b, 0x94, 0x9e)),
                FontSize   = 12,
                Width      = 110
            });
            var hex = color.TrimStart('#');
            byte r = Convert.ToByte(hex[..2], 16);
            byte g = Convert.ToByte(hex[2..4], 16);
            byte b = Convert.ToByte(hex[4..6], 16);
            sp.Children.Add(new TextBlock
            {
                Text        = value,
                Foreground  = new SolidColorBrush(Color.FromRgb(r, g, b)),
                FontSize    = 12,
                FontWeight  = bold ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap
            });
            return sp;
        }

        private async void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            btnSend.IsEnabled = false;
            btnSkip.IsEnabled = false;
            txtStatus.Text    = "⏳ Отправка на GitHub...";

            var first   = _failures.First();
            string title = $"[Install Failures] Ven4Tools {first.Version} — {_failures.Count} приложений";
            string body  = BuildBody();

            try
            {
                using var github = new GitHubService();
                var (ok, url, error) = await github.CreateIssueAsync(
                    title, body, new[] { "bug", "install-failure" });

                if (ok && url != null)
                {
                    MarkReported();
                    txtStatus.Text      = "✅ Отчёт отправлен";
                    btnSend.Content     = "Открыть на GitHub";
                    btnSend.IsEnabled   = true;
                    btnSend.Click      -= BtnSend_Click;
                    btnSend.Click      += (_, _) =>
                    {
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                        Close();
                    };
                    btnSkip.IsEnabled   = true;
                    btnSkip.Content     = "Закрыть";
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
                _failures.ForEach(f => f.Reported = true);
                var all = Newtonsoft.Json.JsonConvert.DeserializeObject<List<InstallFailure>>(
                    System.IO.File.ReadAllText(InstallFailure.FailuresPath)) ?? new();
                foreach (var a in all)
                    if (_failures.Any(f => f.Timestamp == a.Timestamp && f.AppId == a.AppId))
                        a.Reported = true;
                System.IO.File.WriteAllText(InstallFailure.FailuresPath,
                    Newtonsoft.Json.JsonConvert.SerializeObject(all, Newtonsoft.Json.Formatting.Indented));
            }
            catch { }
        }

        private string BuildBody()
        {
            var sb = new StringBuilder();
            sb.AppendLine("## 📦 Отчёт об ошибках установки\n");
            sb.AppendLine($"**Версия:** `{_failures.First().Version}`  ");
            sb.AppendLine($"**ОС:** {_failures.First().OsVersion}  ");
            sb.AppendLine($"**Сессия:** `{_failures.First().SessionId}`  ");
            sb.AppendLine($"**Машина:** `{_failures.First().MachineName}`\n");
            sb.AppendLine("| Приложение | ID | Метод | Ошибка | Время |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var f in _failures)
            {
                DateTime ts = DateTime.TryParse(f.Timestamp, null,
                    DateTimeStyles.RoundtripKind, out var d)
                    ? d.ToLocalTime() : DateTime.Now;
                sb.AppendLine(
                    $"| {f.AppName} | `{f.AppId}` | {f.Method} | {f.Error} | {ts:dd.MM.yyyy HH:mm} |");
            }
            sb.AppendLine("\n---\n*Отчёт создан автоматически через Ven4Tools Launcher*");
            return sb.ToString();
        }
    }

    // Локальная копия модели
    public class InstallFailure
    {
        public static readonly string FailuresPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "failed_installs.json");

        public string SessionId  { get; set; } = "";
        public string MachineName{ get; set; } = "";
        public string AppName    { get; set; } = "";
        public string AppId      { get; set; } = "";
        public string Method     { get; set; } = "";
        public string Error      { get; set; } = "";
        public string Version    { get; set; } = "";
        public string OsVersion  { get; set; } = "";
        public string Timestamp  { get; set; } = "";
        public bool   Reported   { get; set; }
    }
}
