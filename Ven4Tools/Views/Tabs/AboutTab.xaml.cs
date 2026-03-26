using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace Ven4Tools.Views.Tabs
{
    public partial class AboutTab : UserControl
    {
        public event Action<string>? LogMessage;
        
        public AboutTab()
        {
            InitializeComponent();
            
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            txtVersion.Text = $"Версия {version?.ToString() ?? "2.3.0"}";
            
            btnGitHub.Click += BtnGitHub_Click;
            btnFeedback.Click += BtnFeedback_Click;
            btnReportIssue.Click += BtnReportIssue_Click;
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
                AddLog("🌐 Открыт GitHub репозиторий");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка: {ex.Message}");
            }
        }
        
        private void BtnFeedback_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var osVersion = Environment.OSVersion.VersionString;
                var appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "2.3.0";
                
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
                
                AddLog("📧 Открыта форма обратной связи");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка открытия обратной связи: {ex.Message}");
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
                var appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "2.3.0";
                
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
                
                AddLog("🐛 Открыта форма сообщения о проблеме");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка: {ex.Message}");
            }
        }
        
        private string GetLastLogLines(int lines = 30)
        {
            try
            {
                var logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Ven4Tools", "logs", $"install_{DateTime.Now:yyyy-MM-dd}.log");
                
                if (!File.Exists(logPath)) return "Лог не найден";
                
                var allLines = File.ReadAllLines(logPath);
                var lastLines = allLines.Skip(Math.Max(0, allLines.Length - lines)).Take(lines).ToArray();
                return string.Join("\n", lastLines);
            }
            catch
            {
                return "Не удалось прочитать лог";
            }
        }
        
        private void AddLog(string message)
        {
            LogMessage?.Invoke(message);
        }
    }
}
