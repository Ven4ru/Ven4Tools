using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Вкладка «О программе» — версия, история изменений каталога, три
    /// кнопки-ссылки, чтение хвоста лога для отчёта об ошибке. Перенесено из
    /// code-behind при переходе на MVVM (2026-08-25, третья вкладка после
    /// пилота DebloaterTab и HistoryTab), поведение не менялось.
    /// </summary>
    public sealed class AboutViewModel : INotifyPropertyChanged
    {
        public string VersionText { get; }

        private List<ChangelogEntryViewModel> _changelogEntries = new();
        public List<ChangelogEntryViewModel> ChangelogEntries
        {
            get => _changelogEntries;
            private set => SetField(ref _changelogEntries, value);
        }

        public bool HasChangelog => ChangelogEntries.Count > 0;
        public bool NoChangelog => !HasChangelog;

        public RelayCommand GitHubCommand { get; }
        public RelayCommand FeedbackCommand { get; }
        public RelayCommand ReportIssueCommand { get; }

        public AboutViewModel()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText = $"Версия {version?.ToString() ?? "—"}";

            GitHubCommand = new RelayCommand(_ => OpenGitHub());
            FeedbackCommand = new RelayCommand(_ => OpenFeedback());
            ReportIssueCommand = new RelayCommand(_ => OpenReportIssue());

            RefreshChangelog();
        }

        /// <summary>
        /// Перестраивает список изменений каталога. Вызывается из конструктора
        /// и заново — из code-behind при событии CatalogReady (тот же паттерн,
        /// что и раньше: если каталог уже загружен на момент открытия вкладки,
        /// список должен обновиться).
        /// </summary>
        public void RefreshChangelog()
        {
            var entries = CatalogLoaderService.State.Catalog?.Changelog;

            ChangelogEntries = entries == null || entries.Count == 0
                ? new()
                : entries.OrderByDescending(e => e.Version)
                         .Select(e => new ChangelogEntryViewModel(e))
                         .ToList();

            // HasChangelog/NoChangelog вычисляются из ChangelogEntries — без явного
            // уведомления привязки видимости панели остались бы в состоянии на момент
            // открытия вкладки (каталог часто догружается уже после этого).
            OnPropertyChanged(nameof(HasChangelog));
            OnPropertyChanged(nameof(NoChangelog));
        }

        private void OpenGitHub()
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

        private void OpenFeedback()
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

        private void OpenReportIssue()
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

        internal string GetLastLogLines(int lines = 15)
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

                var allLines = File.ReadAllLines(logPath);
                return FormatLastLines(allLines, lines);
            }
            catch
            {
                return "Не удалось прочитать лог";
            }
        }

        /// <summary>
        /// Чистая функция форматирования хвоста лога — вынесена из
        /// <see cref="GetLastLogLines"/>, чтобы дать юнит-тестам шов для
        /// проверки логики обрезки (по числу строк и по числу символов) без
        /// обращения к реальному файлу на диске. Семантика не менялась: та же
        /// обрезка по количеству строк, тот же лимит символов, та же пометка
        /// "(лог обрезан, ...)" при срабатывании любого из двух лимитов.
        /// </summary>
        internal static string FormatLastLines(string[] allLines, int lines)
        {
            // L11: лог кодируется в URL GitHub issue — ограничиваем и число строк, и общий
            // объём символов, чтобы не превысить лимит URL и не обрезаться молча. Факт
            // обрезки явно помечаем в тексте.
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

        // ── INotifyPropertyChanged ───────────────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
