using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;
using Ven4Tools.Services.WindowsUpdate;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Раздел «Ремонт»: проверка системных файлов (DISM RestoreHealth + sfc /scannow)
    /// и сброс Центра обновления (переименование SoftwareDistribution и catroot2).
    /// </summary>
    public sealed partial class WindowsUpdateViewModel
    {
        public RelayCommand RunSystemFileCheckCommand { get; private set; } = null!;
        public RelayCommand ResetWindowsUpdateCommand { get; private set; } = null!;
        public RelayCommand OpenRepairLogsCommand { get; private set; } = null!;

        private string _repairStatusText = "";
        public string RepairStatusText { get => _repairStatusText; private set => SetField(ref _repairStatusText, value); }

        private bool _isRepairRunning;
        public bool IsRepairRunning { get => _isRepairRunning; private set => SetField(ref _isRepairRunning, value); }

        private double _repairProgress;
        public double RepairProgress { get => _repairProgress; private set => SetField(ref _repairProgress, value); }

        private bool _isRepairProgressIndeterminate;
        public bool IsRepairProgressIndeterminate { get => _isRepairProgressIndeterminate; private set => SetField(ref _isRepairProgressIndeterminate, value); }

        private void InitRepairCommands()
        {
            RunSystemFileCheckCommand = RelayCommand.FromAsync(_ => RunSystemFileCheckAsync(), _ => !IsSystemTaskRunning);
            ResetWindowsUpdateCommand = RelayCommand.FromAsync(_ => RunResetWindowsUpdateAsync(), _ => !IsSystemTaskRunning);
            OpenRepairLogsCommand = new RelayCommand(_ => OpenRepairLogs());
        }

        // ── Проверка системных файлов ───────────────────────────────────────────

        private async Task RunSystemFileCheckAsync()
        {
            const string caption = "Проверка системных файлов";
            if (!CanStartSystemTask(caption, needsServicingExclusive: true)) return;
            var answer = MessageBox.Show(
                "Проверить и восстановить системные файлы Windows?\n\n" +
                "1. DISM /RestoreHealth — восстановит хранилище компонентов, при необходимости скачав исправные файлы через Центр обновления.\n" +
                "2. sfc /scannow — проверит системные файлы и заменит повреждённые.\n\n" +
                "Это долго — обычно 10–30 минут, иногда дольше. Компьютером можно пользоваться, но лучше не выключать его до конца проверки. " +
                "Помогает не всегда: если повреждено что-то за пределами системных файлов, проверка этого не исправит.",
                caption, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            var ct = BeginSystemTask(cancellable: true);
            IsRepairRunning = true;
            RepairProgress = 0;
            IsRepairProgressIndeterminate = false;
            AppLogger.Write("[Ремонт] Запуск проверки системных файлов (DISM /RestoreHealth + sfc /scannow)");

            DismOperationResult dism;
            SfcVerdict sfcVerdict = SfcVerdict.Unknown;
            bool cancelled = false;
            string sfcError = "";
            try
            {
                RepairStatusText = "⏳ Шаг 1 из 2: DISM восстанавливает хранилище компонентов...";
                dism = await SystemFileRepairService.RunRestoreHealthAsync(OnRepairPercent, ct);
                cancelled = dism.Cancelled;

                // sfc запускается и после ошибки DISM: хранилище могло быть исправным,
                // а ошибка — только в загрузке (нет интернета). Не запускается лишь после отмены.
                if (!cancelled)
                {
                    RepairProgress = 0;
                    RepairStatusText = "⏳ Шаг 2 из 2: sfc проверяет системные файлы...";
                    var sfc = await SystemFileRepairService.RunSfcAsync(OnRepairPercent, ct);
                    sfcVerdict = sfc.Verdict;
                    cancelled = sfc.Cancelled;
                    sfcError = sfc.Error;
                }
            }
            finally
            {
                IsRepairRunning = false;
                EndSystemTask();
            }

            if (cancelled)
            {
                RepairStatusText = "Проверка прервана.";
                AppLogger.Write("[Ремонт] Проверка системных файлов прервана пользователем");
                return;
            }

            string summary = BuildSystemFileCheckSummary(dism, sfcVerdict, sfcError);
            RepairStatusText = summary;
            AppLogger.Write("[Ремонт] Итог проверки: " + summary.Replace('\n', ' '));
            bool bad = !dism.Success || sfcVerdict is SfcVerdict.NotAllRepaired or SfcVerdict.CouldNotRun or SfcVerdict.Unknown;
            MessageBox.Show(summary, caption, MessageBoxButton.OK, bad ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        /// <summary>Итог двух шагов одним текстом. Чистая функция — для тестов.</summary>
        internal static string BuildSystemFileCheckSummary(DismOperationResult dism, SfcVerdict sfc, string sfcError)
        {
            var sb = new StringBuilder();
            sb.Append(dism.Success
                ? "Хранилище компонентов (DISM): в порядке или восстановлено."
                : $"Хранилище компонентов (DISM): {dism.Message}");
            sb.Append('\n');
            sb.Append(string.IsNullOrEmpty(sfcError) ? SfcResultClassifier.Describe(sfc) : sfcError);
            if (sfc is SfcVerdict.NotAllRepaired or SfcVerdict.CouldNotRun or SfcVerdict.Unknown || !dism.Success)
                sb.Append($"\n\nЖурналы: {SystemFileRepairService.CbsLogPath}, {SystemFileRepairService.DismLogPath}.");
            return sb.ToString();
        }

        private void OnRepairPercent(double percent)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) RepairProgress = percent;
            else dispatcher.BeginInvoke(() => RepairProgress = percent);
        }

        private static void OpenRepairLogs()
        {
            try
            {
                string folder = Path.GetDirectoryName(SystemFileRepairService.CbsLogPath)!;
                Process.Start(new ProcessStartInfo
                {
                    FileName = TrustedExecutablePaths.ExplorerExe,
                    ArgumentList = { folder },
                    UseShellExecute = false
                })?.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Ремонт] Не удалось открыть папку журналов: {ex.Message}");
            }
        }

        // ── Сброс Центра обновления ─────────────────────────────────────────────

        private async Task RunResetWindowsUpdateAsync()
        {
            const string caption = "Сброс Центра обновления";
            if (!CanStartSystemTask(caption, needsServicingExclusive: false)) return;
            var answer = MessageBox.Show(
                "Сбросить Центр обновления Windows?\n\n" +
                "• Будут остановлены службы wuauserv, bits, cryptsvc, msiserver.\n" +
                "• Папки C:\\Windows\\SoftwareDistribution и C:\\Windows\\System32\\catroot2 будут переименованы в *.old-<дата> " +
                "(не удалены — их можно вернуть).\n" +
                "• Службы будут запущены снова, Windows создаст папки заново.\n\n" +
                "Список «Журнал обновлений» в параметрах Windows обнулится (сами обновления останутся установленными), " +
                "а недокачанные обновления скачаются заново.\n\n" +
                "Помогает, когда обновления не скачиваются или не ставятся с одной и той же ошибкой, — но не всегда.",
                caption, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            BeginSystemTask(cancellable: false);
            IsRepairRunning = true;
            IsRepairProgressIndeterminate = true;
            RepairStatusText = "⏳ Сброс Центра обновления: остановка служб и переименование папок...";
            AppLogger.Write("[Ремонт] Запуск сброса Центра обновления");
            WuResetResult result;
            try
            {
                result = await Task.Run(() => new WindowsUpdateResetService().Reset());
            }
            finally
            {
                IsRepairRunning = false;
                IsRepairProgressIndeterminate = false;
                EndSystemTask();
            }

            string text = result.Message;
            if (result.NotStarted.Count > 0)
                text += "\n\nНе запустились службы: " + string.Join(", ", result.NotStarted) +
                        ". Обычно их запускает перезагрузка компьютера.";
            RepairStatusText = (result.Success ? "✅ " : "❌ ") + text;
            AppLogger.Write("[Ремонт] Сброс Центра обновления: " + text.Replace('\n', ' '));

            if (result.Success)
            {
                // Прежний список найденных патчей больше не соответствует состоянию агента.
                _foundItems.Clear();
                _hasSearchResult = false;
                Tree = Array.Empty<WindowsUpdateCategoryNode>();
                UpdateSelectionSummary();
                ShowEmptyStateInfo("Центр обновления сброшен", "Нажмите «Проверить обновления», чтобы найти обновления заново", offerDiagnostics: false);
                if (result.Renamed.Count > 0)
                    text += "\n\nКогда обновления снова начнут ставиться, старые папки " +
                            string.Join(", ", result.Renamed.Select(r => Path.GetFileName(r.To))) +
                            " можно удалить вручную, чтобы освободить место.";
            }
            MessageBox.Show(text, caption, MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
    }
}
