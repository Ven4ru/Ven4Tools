using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;
using Ven4Tools.Services.Diagnostics;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// «Синий экран понятным языком»: расшифровка кода по словарю, параметры 1–4,
    /// «возможная связь» с драйверами, поставленными перед первым сбоем, и папка минидампов.
    /// Только чтение — сами дампы не разбираются.
    /// </summary>
    public sealed partial class DiagnosticsViewModel
    {
        private const int MaxDriverHintRows = 12;

        private string _bsodDriverHintText = "";
        /// <summary>Список драйверов «возможной связи»; пусто — блок скрыт.</summary>
        public string BsodDriverHintText { get => _bsodDriverHintText; private set => SetField(ref _bsodDriverHintText, value); }

        private string _bsodDriverHintHeader = "";
        public string BsodDriverHintHeader { get => _bsodDriverHintHeader; private set => SetField(ref _bsodDriverHintHeader, value); }

        private bool _showBsodDriverHint;
        public bool ShowBsodDriverHint { get => _showBsodDriverHint; private set => SetField(ref _showBsodDriverHint, value); }

        private string _minidumpText = "";
        public string MinidumpText { get => _minidumpText; private set => SetField(ref _minidumpText, value); }

        private bool _showMinidumpBlock;
        public bool ShowMinidumpBlock { get => _showMinidumpBlock; private set => SetField(ref _showMinidumpBlock, value); }

        private bool _showOpenMinidumpButton;
        public bool ShowOpenMinidumpButton { get => _showOpenMinidumpButton; private set => SetField(ref _showOpenMinidumpButton, value); }

        // Команды создаются при первом обращении: так этому файлу не нужен общий конструктор.
        private RelayCommand? _openMinidumpFolderCommand;
        public RelayCommand OpenMinidumpFolderCommand => _openMinidumpFolderCommand ??= new RelayCommand(_ => OpenMinidumpFolder());

        private RelayCommand? _openBugCheckInfoCommand;
        public RelayCommand OpenBugCheckInfoCommand => _openBugCheckInfoCommand ??= new RelayCommand(p => OpenBugCheckInfo(p as string));

        // Снимок для отчёта «Скопировать полный отчёт».
        private List<DriverInstallRecord> _lastBsodDrivers = new();
        private DateTime? _lastFirstBsodTime;
        private MinidumpFolderInfo? _lastMinidumpInfo;

        private void ResetBsodExtras()
        {
            BsodDriverHintText = "";
            BsodDriverHintHeader = "";
            ShowBsodDriverHint = false;
            MinidumpText = "";
            ShowMinidumpBlock = false;
            ShowOpenMinidumpButton = false;
            _lastBsodDrivers = new List<DriverInstallRecord>();
            _lastFirstBsodTime = null;
            _lastMinidumpInfo = null;
        }

        private static RebootCardInfo BuildBsodCard(RebootDiagnosis d, BugCheckDetails bc, string icon)
        {
            var description = BugCheckDictionary.Embedded.Lookup(bc.Code);
            string code = BugCheckDictionary.FormatCode(bc.Code);

            string header = description != null
                ? $"{icon} {d.TimeCreated:g} — Синий экран: {description.Name} ({code})"
                : $"{icon} {d.TimeCreated:g} — Синий экран: код {code}";

            return new RebootCardInfo
            {
                Header = header,
                RawDetails = d.RawDetails,
                BsodTitle = description != null
                    ? $"{description.Name} · {code}"
                    : $"Код {code} — не из словаря Ven4Tools. Описание кода — на сайте Microsoft.",
                BsodCause = description?.Cause ?? "",
                BsodCheck = description?.Check ?? "",
                BsodParameters = bc.Parameters.Count > 0 ? BugCheckService.FormatParameters(bc.Parameters) : "",
                BsodDumpPath = bc.DumpPath ?? "",
                BsodInfoUrl = BugCheckDictionary.MicrosoftInfoUrl(bc.Code, description?.Name)
            };
        }

        private async Task RunBsodExtrasAsync(IReadOnlyList<RebootDiagnosis> diagnoses)
        {
            var bsods = diagnoses.Where(x => x.Category == RebootCategory.Bsod).ToList();
            if (bsods.Count == 0) return;

            DateTime first = bsods.Min(x => x.TimeCreated);
            List<DriverInstallRecord> drivers;
            MinidumpFolderInfo minidump;
            try
            {
                (drivers, minidump) = await Task.Run(() =>
                    (BugCheckService.GetDriverInstalls(first - BugCheckService.DriverLookback, first),
                     BugCheckService.GetMinidumpInfo()));
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.RunBsodExtrasAsync");
                return;
            }

            _lastFirstBsodTime = first;
            _lastBsodDrivers = drivers;
            _lastMinidumpInfo = minidump;

            if (drivers.Count > 0)
            {
                // Формулировка строго «возможная связь»: совпадение по времени не доказывает вину.
                BsodDriverHintHeader = $"🔎 Возможная связь: за 3 дня до первого синего экрана ({first:g}) ставились или обновлялись драйверы. Это не значит, что виноваты именно они, но проверку стоит начать с них — например, откатить драйвер в «Диспетчере устройств».";
                var lines = drivers.Take(MaxDriverHintRows).Select(r => $"• {r.Time:g} — {r.Name}").ToList();
                if (drivers.Count > MaxDriverHintRows)
                    lines.Add($"… и ещё {drivers.Count - MaxDriverHintRows}");
                BsodDriverHintText = string.Join(Environment.NewLine, lines);
                ShowBsodDriverHint = true;
            }

            ShowMinidumpBlock = true;
            if (!minidump.Exists)
            {
                MinidumpText = $"Папка минидампов не найдена ({minidump.FolderPath}). Windows могла не сохранить дамп: это настраивается в «Параметры системы → Дополнительно → Загрузка и восстановление».";
            }
            else if (minidump.FileCount == 0)
            {
                MinidumpText = $"В папке минидампов ({minidump.FolderPath}) файлов нет.";
                ShowOpenMinidumpButton = true;
            }
            else
            {
                MinidumpText = $"Минидампов в {minidump.FolderPath}: {minidump.FileCount}, последний — {minidump.LatestFileTime:g}. Их можно передать мастеру или разобрать программой WinDbg от Microsoft.";
                ShowOpenMinidumpButton = true;
            }
        }

        private void OpenMinidumpFolder()
        {
            try
            {
                string folder = BugCheckService.MinidumpFolder;
                if (!System.IO.Directory.Exists(folder))
                {
                    MessageBox.Show("Папка минидампов не найдена.", "Синий экран", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                Process.Start(TrustedExecutablePaths.ExplorerExe, $"\"{folder}\"");
                AppLogger.Write($"📁 Открыта папка минидампов: {folder}");
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.OpenMinidumpFolder");
            }
        }

        private static void OpenBugCheckInfo(string? url)
        {
            // Открываем только свою же ссылку на документацию Microsoft.
            if (string.IsNullOrEmpty(url) || !url.StartsWith("https://learn.microsoft.com/", StringComparison.Ordinal)) return;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.OpenBugCheckInfo");
                MessageBox.Show("Не удалось открыть браузер: " + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Секция «Синие экраны: расшифровка» для «Скопировать полный отчёт».</summary>
        private void AppendBsodReport(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("--- Синие экраны: расшифровка ---");
            var bsods = _lastRebootDiagnoses.Where(x => x.Category == RebootCategory.Bsod && x.BugCheck != null).ToList();
            if (bsods.Count == 0)
            {
                sb.AppendLine("Синих экранов за последние 7 дней не найдено (или диагностика ещё не запускалась).");
                return;
            }

            foreach (var d in bsods)
            {
                var bc = d.BugCheck!;
                var description = BugCheckDictionary.Embedded.Lookup(bc.Code);
                string code = BugCheckDictionary.FormatCode(bc.Code);
                sb.AppendLine(description != null
                    ? $"{d.TimeCreated:g} — {code} {description.Name}"
                    : $"{d.TimeCreated:g} — {code} (код не из словаря)");
                if (description != null)
                {
                    sb.AppendLine($"  Частые причины: {description.Cause}");
                    sb.AppendLine($"  Что проверить: {description.Check}");
                }
                if (bc.Parameters.Count > 0)
                    sb.AppendLine($"  Параметры 1–4: {BugCheckService.FormatParameters(bc.Parameters)}");
                if (!string.IsNullOrEmpty(bc.DumpPath))
                    sb.AppendLine($"  Дамп: {CrashReportService.SanitizePath(bc.DumpPath)}");
            }

            if (_lastFirstBsodTime != null)
            {
                if (_lastBsodDrivers.Count == 0)
                {
                    sb.AppendLine($"Возможная связь: за 3 дня до первого сбоя ({_lastFirstBsodTime:g}) установок драйверов не найдено.");
                }
                else
                {
                    sb.AppendLine($"Возможная связь (не доказательство): драйверы, поставленные за 3 дня до первого сбоя ({_lastFirstBsodTime:g}):");
                    foreach (var r in _lastBsodDrivers)
                        sb.AppendLine($"  {r.Time:g} — {CrashReportService.SanitizePath(r.Name)}");
                }
            }
            if (_lastMinidumpInfo != null)
            {
                sb.AppendLine(_lastMinidumpInfo.Exists
                    ? $"Минидампы: {_lastMinidumpInfo.FileCount} в {_lastMinidumpInfo.FolderPath}" +
                      (_lastMinidumpInfo.LatestFileTime is DateTime t ? $", последний — {t:g}" : "")
                    : $"Минидампы: папка {_lastMinidumpInfo.FolderPath} не найдена");
            }
        }
    }
}
