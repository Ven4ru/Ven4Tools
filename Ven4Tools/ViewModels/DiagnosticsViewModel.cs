using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Ven4Tools.Helpers;

namespace Ven4Tools.ViewModels
{
    /// <summary>Одна строка-результат (диски / ошибки Windows Update) — текст и цвет.</summary>
    public sealed class DiagnosticsTextRow
    {
        public required string Text { get; init; }
        public required Brush Foreground { get; init; }
    }

    /// <summary>Одна карточка нештатного завершения работы (история перезагрузок).</summary>
    public sealed class RebootCardInfo
    {
        public required string Header { get; init; }
        public required string RawDetails { get; init; }
    }

    /// <summary>
    /// ViewModel вкладки «Диагностика». Логика перенесена из code-behind при
    /// MVVM-миграции (2026-08-26, восьмая вкладка после Debloater/History/About/
    /// Activation/Network/Office/Installed) без изменения поведения.
    /// Разбит на partial-файлы по образцу OfficeViewModel.*/InstalledViewModel.*.
    /// </summary>
    public sealed partial class DiagnosticsViewModel : ViewModelBase
    {
        public event Action? GoToWindowsUpdate;

        // ── Информация о системе / логи ─────────────────────────────────────────

        private string _osVersionText = "Загрузка...";
        public string OSVersionText { get => _osVersionText; private set => SetField(ref _osVersionText, value); }

        private string _processorText = "Загрузка...";
        public string ProcessorText { get => _processorText; private set => SetField(ref _processorText, value); }

        private string _ramText = "Загрузка...";
        public string RAMText { get => _ramText; private set => SetField(ref _ramText, value); }

        private string _appVersionText = "";
        public string AppVersionText { get => _appVersionText; private set => SetField(ref _appVersionText, value); }

        private string _latestLogText = "Нажмите «Последний лог» для просмотра...";
        public string LatestLogText { get => _latestLogText; private set => SetField(ref _latestLogText, value); }

        // ── Статус-бейдж ─────────────────────────────────────────────────────────

        private string _healthBadgeText = "Диагностика ещё не запускалась";
        public string HealthBadgeText { get => _healthBadgeText; private set => SetField(ref _healthBadgeText, value); }

        private Brush _healthBadgeBrush = BrushResolver.Resolve("TextSecondary");
        public Brush HealthBadgeBrush { get => _healthBadgeBrush; private set => SetField(ref _healthBadgeBrush, value); }

        private string _lastRunText = "";
        public string LastRunText { get => _lastRunText; private set => SetField(ref _lastRunText, value); }

        // ── Разделы результатов ──────────────────────────────────────────────────

        private bool _showPlaceholders = true;
        public bool ShowPlaceholders { get => _showPlaceholders; private set => SetField(ref _showPlaceholders, value); }

        private IReadOnlyList<DiagnosticsTextRow> _diskRows = Array.Empty<DiagnosticsTextRow>();
        public IReadOnlyList<DiagnosticsTextRow> DiskRows { get => _diskRows; private set => SetField(ref _diskRows, value); }

        private DiagnosticsTextRow? _rebootStatusRow;
        public DiagnosticsTextRow? RebootStatusRow { get => _rebootStatusRow; private set => SetField(ref _rebootStatusRow, value); }

        private bool _showRebootStatusRow;
        public bool ShowRebootStatusRow { get => _showRebootStatusRow; private set => SetField(ref _showRebootStatusRow, value); }

        private IReadOnlyList<RebootCardInfo> _rebootCards = Array.Empty<RebootCardInfo>();
        public IReadOnlyList<RebootCardInfo> RebootCards { get => _rebootCards; private set => SetField(ref _rebootCards, value); }

        private bool _showDisableFastStartupButton;
        public bool ShowDisableFastStartupButton { get => _showDisableFastStartupButton; private set => SetField(ref _showDisableFastStartupButton, value); }

        private IReadOnlyList<DiagnosticsTextRow> _wuRows = Array.Empty<DiagnosticsTextRow>();
        public IReadOnlyList<DiagnosticsTextRow> WuRows { get => _wuRows; private set => SetField(ref _wuRows, value); }

        private bool _wuButtonsVisible;
        public bool WuButtonsVisible { get => _wuButtonsVisible; private set => SetField(ref _wuButtonsVisible, value); }

        private string _hardwareSummaryText = "Нажмите «Запустить диагностику»";
        public string HardwareSummaryText { get => _hardwareSummaryText; private set => SetField(ref _hardwareSummaryText, value); }

        private string _hardwareRawText = "";
        public string HardwareRawText { get => _hardwareRawText; private set => SetField(ref _hardwareRawText, value); }

        private bool _hardwareRawVisible;
        public bool HardwareRawVisible { get => _hardwareRawVisible; private set => SetField(ref _hardwareRawVisible, value); }

        // ── Turbo Boost ──────────────────────────────────────────────────────────

        private string _turboBoostStatusText = "Текущее состояние: определяется...";
        public string TurboBoostStatusText { get => _turboBoostStatusText; private set => SetField(ref _turboBoostStatusText, value); }

        // ── Busy-флаги команд ────────────────────────────────────────────────────

        private bool _isRunningDiagnostics;
        public bool IsRunningDiagnostics
        {
            get => _isRunningDiagnostics;
            private set { if (SetField(ref _isRunningDiagnostics, value)) RunDiagnosticsCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isClearingWuCache;
        public bool IsClearingWuCache
        {
            get => _isClearingWuCache;
            private set { if (SetField(ref _isClearingWuCache, value)) ClearWuCacheCommand.RaiseCanExecuteChanged(); }
        }

        /// <summary>
        /// Общий флаг для «Отключить»/«Включить» турбобуст: обе кнопки правят одну и ту
        /// же настройку схемы электропитания через powercfg, поэтому взаимно исключают
        /// друг друга, а не только сами себя. internal set — тем же способом, что у
        /// <c>NetworkViewModel</c>, флаг доступен юнит-тестам.
        /// </summary>
        private bool _isApplyingTurboBoost;
        public bool IsApplyingTurboBoost
        {
            get => _isApplyingTurboBoost;
            internal set
            {
                SetField(ref _isApplyingTurboBoost, value);
                DisableTurboBoostCommand.RaiseCanExecuteChanged();
                EnableTurboBoostCommand.RaiseCanExecuteChanged();
            }
        }

        /// <summary>
        /// Отключение быстрого запуска — отдельная операция (powercfg /h off),
        /// с турбобустом не пересекается, поэтому и флаг у неё свой.
        /// </summary>
        private bool _isDisablingFastStartup;
        public bool IsDisablingFastStartup
        {
            get => _isDisablingFastStartup;
            internal set { if (SetField(ref _isDisablingFastStartup, value)) DisableFastStartupCommand.RaiseCanExecuteChanged(); }
        }

        // ── Команды ──────────────────────────────────────────────────────────────

        public RelayCommand RunDiagnosticsCommand { get; }
        public RelayCommand CopySystemInfoCommand { get; }
        public RelayCommand OpenLogsCommand { get; }
        public RelayCommand OpenLatestLogCommand { get; }
        public RelayCommand ClearLogsCommand { get; }
        public RelayCommand DisableTurboBoostCommand { get; }
        public RelayCommand EnableTurboBoostCommand { get; }
        public RelayCommand ClearWuCacheCommand { get; }
        public RelayCommand OpenWindowsUpdateCommand { get; }
        public RelayCommand CopyFullReportCommand { get; }
        public RelayCommand DisableFastStartupCommand { get; }

        public DiagnosticsViewModel()
        {
            RunDiagnosticsCommand     = RelayCommand.FromAsync(_ => RunDiagnosticsAsync(),    _ => !IsRunningDiagnostics);
            CopySystemInfoCommand     = new RelayCommand(_ => CopySystemInfo());
            OpenLogsCommand           = new RelayCommand(_ => OpenLogs());
            OpenLatestLogCommand      = new RelayCommand(_ => OpenLatestLog());
            ClearLogsCommand          = new RelayCommand(_ => ClearLogs());
            DisableTurboBoostCommand  = RelayCommand.FromAsync(_ => RunDisableTurboBoostAsync(), _ => !IsApplyingTurboBoost);
            EnableTurboBoostCommand   = RelayCommand.FromAsync(_ => RunEnableTurboBoostAsync(),  _ => !IsApplyingTurboBoost);
            ClearWuCacheCommand       = RelayCommand.FromAsync(_ => RunClearWuCacheAsync(),    _ => !IsClearingWuCache);
            OpenWindowsUpdateCommand  = new RelayCommand(_ => GoToWindowsUpdate?.Invoke());
            CopyFullReportCommand     = new RelayCommand(_ => CopyFullReport());
            DisableFastStartupCommand = RelayCommand.FromAsync(_ => RunDisableFastStartupAsync(), _ => !IsDisablingFastStartup);
        }

        public async Task InitializeAsync()
        {
            await LoadSystemInfoAsync();
            await RefreshTurboBoostStatusAsync();
        }
    }
}
