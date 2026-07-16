using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class AboutTab : UserControl
    {
        public AboutTab()
        {
            InitializeComponent();

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            txtVersion.Text = $"Версия {version?.ToString() ?? "—"}";

            btnGitHub.Click += BtnGitHub_Click;
            btnFeedback.Click += BtnFeedback_Click;
            btnReportIssue.Click += BtnReportIssue_Click;

            PopulateChangelog();

            Loaded += (_, _) =>
            {
                CatalogLoaderService.CatalogReady += OnCatalogReady;
                // Обновляем changelog если каталог уже был загружен до открытия вкладки
                if (CatalogLoaderService.LoadedCatalog != null)
                {
                    pnlChangelog.Children.Clear();
                    PopulateChangelog();
                }
            };
            Unloaded += (_, _) => CatalogLoaderService.CatalogReady -= OnCatalogReady;
        }

        private void OnCatalogReady(Models.MasterCatalog _)
        {
            Dispatcher.Invoke(() =>
            {
                pnlChangelog.Children.Clear();
                PopulateChangelog();
            });
        }

        private void PopulateChangelog()
        {
            var entries = CatalogLoaderService.LoadedCatalog?.Changelog;

            if (entries == null || entries.Count == 0)
            {
                pnlChangelog.Children.Add(new TextBlock
                {
                    Text = "История изменений будет доступна после загрузки каталога.",
                    Foreground = (Brush)FindResource("TextSecondary"),
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            foreach (var entry in entries.OrderByDescending(e => e.Version))
            {
                var block = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

                block.Children.Add(new TextBlock
                {
                    Text = $"v{entry.Version}  ·  {entry.Date}",
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("TextPrimary")
                });

                if (!string.IsNullOrEmpty(entry.Message))
                    block.Children.Add(new TextBlock
                    {
                        Text = entry.Message,
                        Foreground = (Brush)FindResource("TextSecondary"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 0)
                    });

                if (entry.AddedApps?.Count > 0)
                    block.Children.Add(new TextBlock
                    {
                        Text = $"+ {string.Join(", ", entry.AddedApps)}",
                        Foreground = new SolidColorBrush(Color.FromRgb(100, 200, 100)),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 0),
                        FontSize = 11
                    });

                pnlChangelog.Children.Add(block);
            }
        }
        
        private void BtnGitHub_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/Ven4ru/Ven4Tools",
                    UseShellExecute = true
                });
                AppLogger.Write("🌐 Открыт GitHub репозиторий");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка: {ex.Message}");
            }
        }
        
        private void BtnFeedback_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var osVersion = Environment.OSVersion.VersionString;
                var appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "—";
                
                var title = Uri.EscapeDataString($"Обратная связь: {appVersion}");
                var body = Uri.EscapeDataString(
                    $"## Версия\n{appVersion}\n\n" +
                    $"## ОС\n{osVersion}\n\n" +
                    $"## Сообщение\n\n");
                
                var url = $"https://github.com/Ven4ru/Ven4Tools/issues/new?title={title}&body={body}";
                
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
                
                AppLogger.Write("📧 Открыта форма обратной связи");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка открытия обратной связи: {ex.Message}");
                MessageBox.Show("Не удалось открыть форму обратной связи.\n" +
                                "Пожалуйста, напишите на GitHub вручную:\n" +
                                "https://github.com/Ven4ru/Ven4Tools/issues",
                                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        
        private void BtnReportIssue_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var osVersion = Environment.OSVersion.VersionString;
                var appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "—";
                
                string lastLogs = GetLastLogLines();
                
                var title = Uri.EscapeDataString($"[BUG] Проблема в версии {appVersion}");
                var body = Uri.EscapeDataString(
                    $"## Описание проблемы\n\n" +
                    $"### Шаги воспроизведения\n1. \n2. \n3. \n\n" +
                    $"### Ожидаемое поведение\n\n" +
                    $"### Фактическое поведение\n\n" +
                    $"## Системная информация\n" +
                    $"Версия: {appVersion}\n" +
                    $"ОС: {osVersion}\n\n" +
                    $"## Последние логи\n```\n{lastLogs}\n```");
                
                var url = $"https://github.com/Ven4ru/Ven4Tools/issues/new?title={title}&body={body}";
                
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
                
                AppLogger.Write("🐛 Открыта форма сообщения о проблеме");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка: {ex.Message}");
            }
        }
        
        private string GetLastLogLines(int lines = 15)
        {
            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Ven4Tools", "logs");

                if (!Directory.Exists(logDir)) return "Лог не найден";

                var logPath = Directory.GetFiles(logDir, "install_*.log")
                    .OrderByDescending(f => f)
                    .FirstOrDefault();

                if (logPath == null) return "Лог не найден";

                // L11: лог кодируется в URL GitHub issue — ограничиваем и число строк, и общий
                // объём символов, чтобы не превысить лимит URL и не обрезаться молча. Факт
                // обрезки явно помечаем в тексте.
                var allLines = File.ReadAllLines(logPath);
                bool truncated = allLines.Length > lines;
                var lastLines = allLines.Skip(Math.Max(0, allLines.Length - lines)).Take(lines).ToArray();
                string body = CrashReportService.SanitizePath(string.Join("\n", lastLines));

                const int maxChars = 3000;
                if (body.Length > maxChars)
                {
                    body = body.Substring(body.Length - maxChars);
                    truncated = true;
                }
                if (truncated)
                    body = "… (лог обрезан, показаны только последние строки) …\n" + body;
                return body;
            }
            catch
            {
                return "Не удалось прочитать лог";
            }
        }
    }
}
