using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using Ven4Tools.Helpers;
using Ven4Tools.Services;
using Ven4Tools.Services.Diagnostics;

namespace Ven4Tools.ViewModels
{
    /// <summary>Строка чеклиста «Готов ли ПК к Windows 11».</summary>
    public sealed class Win11CheckRow : ViewModelBase
    {
        public required string Icon { get; init; }

        /// <summary>Ключ темы, а не кисть, — по той же причине, что у <see cref="DiagnosticsTextRow"/>.</summary>
        public required string IconForegroundKey { get; init; }
        public Brush IconForeground => BrushResolver.Resolve(IconForegroundKey);

        public required string Title { get; init; }
        public required string Detail { get; init; }
        public string Hint { get; init; } = "";
        public bool HasHint => Hint.Length > 0;

        internal void RefreshThemeBrushes() => OnPropertyChanged(nameof(IconForeground));
    }

    /// <summary>
    /// «Готов ли ПК к Windows 11 и что мешает». Только чтение: ничего не меняет и не
    /// обходит требования — подсказки «как исправить» только текстом.
    /// </summary>
    public sealed partial class DiagnosticsViewModel
    {
        private IReadOnlyList<Win11CheckRow> _win11Rows = Array.Empty<Win11CheckRow>();
        public IReadOnlyList<Win11CheckRow> Win11Rows { get => _win11Rows; private set => SetField(ref _win11Rows, value); }

        private string _win11VerdictText = "Нажмите «Запустить диагностику» или «Проверить» — проверка займёт до минуты.";
        public string Win11VerdictText { get => _win11VerdictText; private set => SetField(ref _win11VerdictText, value); }

        private string _win11VerdictBrushKey = "TextSecondary";
        public Brush Win11VerdictBrush => BrushResolver.Resolve(_win11VerdictBrushKey);

        private void SetWin11VerdictBrush(string themeKey)
        {
            _win11VerdictBrushKey = themeKey;
            OnPropertyChanged(nameof(Win11VerdictBrush));
        }

        private string _win11IntroText = "";
        /// <summary>Пояснение над чеклистом (на Windows 11 — «проверка для справки»); пусто — скрыто.</summary>
        public string Win11IntroText { get => _win11IntroText; private set => SetField(ref _win11IntroText, value); }

        private IReadOnlyList<DiagnosticsInfoLine> _win11WindowsLines = Array.Empty<DiagnosticsInfoLine>();
        /// <summary>Блок «Ваша Windows»: выпуск, версия, сборка, последнее накопительное обновление.</summary>
        public IReadOnlyList<DiagnosticsInfoLine> Win11WindowsLines { get => _win11WindowsLines; private set => SetField(ref _win11WindowsLines, value); }

        private string _win11WindowsNotes = "";
        public string Win11WindowsNotes { get => _win11WindowsNotes; private set => SetField(ref _win11WindowsNotes, value); }

        private bool _showWin11Results;
        public bool ShowWin11Results { get => _showWin11Results; private set => SetField(ref _showWin11Results, value); }

        private bool _isCheckingWin11;
        public bool IsCheckingWin11
        {
            get => _isCheckingWin11;
            private set { if (SetField(ref _isCheckingWin11, value)) CheckWin11Command.RaiseCanExecuteChanged(); }
        }

        // Команды создаются при первом обращении: так этому файлу не нужен общий конструктор.
        private RelayCommand? _checkWin11Command;
        public RelayCommand CheckWin11Command => _checkWin11Command ??=
            RelayCommand.FromAsync(_ => RunWin11ReadinessCheckAsync(), _ => !IsCheckingWin11);

        private RelayCommand? _openWin11CpuListCommand;
        public RelayCommand OpenWin11CpuListCommand => _openWin11CpuListCommand ??= new RelayCommand(_ => OpenWin11CpuList());

        private Win11ReadinessReport? _lastWin11Report;

        private void RefreshWin11Brushes()
        {
            OnPropertyChanged(nameof(Win11VerdictBrush));
            foreach (var row in Win11Rows) row.RefreshThemeBrushes();
        }

        private async Task RunWin11ReadinessCheckAsync()
        {
            if (IsCheckingWin11) return;
            IsCheckingWin11 = true;
            Win11VerdictText = "Проверка выполняется...";
            SetWin11VerdictBrush("TextSecondary");
            try
            {
                var report = await Win11ReadinessService.CheckAsync();
                _lastWin11Report = report;
                ApplyWin11Report(report);
                AppLogger.Write($"🪟 Проверка готовности к Windows 11: {report.Verdict.Kind}, не выполнено {report.Verdict.FailCount}, нет данных {report.Verdict.UnknownCount}");
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.RunWin11ReadinessCheckAsync");
                Win11VerdictText = "Недоступно: проверку выполнить не удалось.";
                SetWin11VerdictBrush("StatusWarning");
            }
            finally
            {
                IsCheckingWin11 = false;
            }
        }

        private void ApplyWin11Report(Win11ReadinessReport report)
        {
            Win11Rows = report.Checks.Select(c => new Win11CheckRow
            {
                Icon = StatusIcon(c.Status),
                IconForegroundKey = StatusBrushKey(c.Status),
                Title = c.Title,
                Detail = c.Detail,
                Hint = c.Hint
            }).ToList();

            var (text, brush) = VerdictText(report.Verdict, report.Windows.IsWindows11);
            Win11VerdictText = text;
            SetWin11VerdictBrush(brush);
            Win11IntroText = report.Windows.IsWindows11
                ? "Этот ПК уже работает на Windows 11 — проверка для справки: она показывает, выполнены ли требования, в том числе инструкции процессора, без которых не запускаются версии 24H2 и новее."
                : "";

            var w = report.Windows;
            Win11WindowsLines = new[]
            {
                new DiagnosticsInfoLine { Label = "Выпуск:", Value = w.ProductName },
                new DiagnosticsInfoLine { Label = "Версия:", Value = w.DisplayVersion.Length > 0 ? w.DisplayVersion : "нет данных" },
                new DiagnosticsInfoLine { Label = "Сборка:", Value = w.Ubr > 0 ? $"{w.Build}.{w.Ubr}" : w.Build.ToString() },
                new DiagnosticsInfoLine
                {
                    Label = "Последнее накопительное обновление:",
                    Value = w.LastCumulativeUpdate is DateTime d ? d.ToString("d") : "нет данных в журнале Windows Update"
                }
            };
            Win11WindowsNotes = string.Join(Environment.NewLine + Environment.NewLine, WindowsVersionInfo.BuildNotes(w, DateTime.Today));
            ShowWin11Results = true;
        }

        internal static string StatusIcon(Win11CheckStatus status) => status switch
        {
            Win11CheckStatus.Pass => "✓",
            Win11CheckStatus.Fail => "✗",
            Win11CheckStatus.Warning => "!",
            _ => "?"
        };

        private static string StatusBrushKey(Win11CheckStatus status) => status switch
        {
            Win11CheckStatus.Pass => "StatusSuccess",
            Win11CheckStatus.Fail => "StatusDanger",
            Win11CheckStatus.Warning => "StatusWarning",
            _ => "TextSecondary"
        };

        /// <summary>Итог чеклиста одной строкой. internal static — проверяется юнит-тестами.</summary>
        internal static (string Text, string BrushKey) VerdictText(Win11Verdict v, bool isWindows11)
        {
            string text;
            string brush;
            switch (v.Kind)
            {
                case Win11VerdictKind.NotReady:
                    text = isWindows11
                        ? $"✗ Не выполнено требований Windows 11: {v.FailCount}. Подробности и что делать — ниже."
                        : $"✗ Пока не готов к Windows 11: мешает пунктов — {v.FailCount}. Что делать — в подсказках ниже.";
                    brush = "StatusDanger";
                    break;
                case Win11VerdictKind.ReadyWithUnknowns:
                    text = $"? Явных препятствий нет, но по пунктам ({v.UnknownCount}) нет данных — проверьте их вручную.";
                    brush = "StatusWarning";
                    break;
                default:
                    text = isWindows11
                        ? "✓ Все требования Windows 11 выполнены."
                        : "✓ ПК подходит для Windows 11.";
                    brush = "StatusSuccess";
                    break;
            }
            if (v.WarningCount > 0) text = Tr(text) + string.Format(Tr(" Замечаний: {0}."), v.WarningCount);
            return (text, brush);
        }

        private static void OpenWin11CpuList()
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = Win11Requirements.CpuListUrlIntel, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.OpenWin11CpuList");
                MessageBox.Show("Не удалось открыть браузер: " + ex.Message, "Ошибка",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        /// <summary>Секция «Готовность к Windows 11» для «Скопировать полный отчёт».</summary>
        private void AppendWin11ReadinessReport(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("--- Готовность к Windows 11 ---");
            if (_lastWin11Report == null)
            {
                sb.AppendLine("Проверка ещё не запускалась.");
                return;
            }
            sb.AppendLine(Win11VerdictText);
            foreach (var c in _lastWin11Report.Checks)
                sb.AppendLine($"[{StatusIcon(c.Status)}] {c.Title}: {c.Detail}");
            var w = _lastWin11Report.Windows;
            sb.AppendLine($"Windows: {w.ProductName}, версия {(w.DisplayVersion.Length > 0 ? w.DisplayVersion : "нет данных")}, сборка {w.Build}.{w.Ubr}");
            sb.AppendLine(w.LastCumulativeUpdate is DateTime d
                ? $"Последнее накопительное обновление: {d:d}"
                : "Последнее накопительное обновление: нет данных");
        }
    }

    /// <summary>Строка «подпись — значение» (блок «Ваша Windows»).</summary>
    public sealed class DiagnosticsInfoLine
    {
        public required string Label { get; init; }
        public required string Value { get; init; }
    }
}
