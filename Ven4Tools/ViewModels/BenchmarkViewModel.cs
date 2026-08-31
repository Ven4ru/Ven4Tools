using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Ven4Tools.Helpers;
using Ven4Tools.Models;
using Ven4Tools.Services.DiskBenchmark;

namespace Ven4Tools.ViewModels
{
    /// <summary>Строка таблицы результатов (один паттерн нагрузки, Read+Write).</summary>
    public sealed class BenchmarkResultRow
    {
        public required int Index { get; init; }
        public required string Name { get; init; }
        public required string ReadValueText { get; init; }
        public required string ReadSubText { get; init; }
        public required string WriteValueText { get; init; }
        public required string WriteSubText { get; init; }

        public string NameAutomationId => $"txtP{Index}Name";
        public string ReadAutomationId => $"txtP{Index}Read";
        public string ReadSubAutomationId => $"txtP{Index}ReadSub";
        public string WriteAutomationId => $"txtP{Index}Write";
        public string WriteSubAutomationId => $"txtP{Index}WriteSub";
    }

    /// <summary>Одна строка текстового вывода («Что это значит»).</summary>
    public sealed class ConclusionLine
    {
        public required string Text { get; init; }
        public required Brush Foreground { get; init; }
    }

    /// <summary>Пункт выпадающего списка накопителей.</summary>
    public sealed class DiskOptionItem
    {
        public required string Label { get; init; }
        public required PhysicalDiskInfo Disk { get; init; }
        public required bool CanBenchmark { get; init; }
    }

    /// <summary>Пункт выпадающего списка томов.</summary>
    public sealed class VolumeOptionItem
    {
        public required string Label { get; init; }
        public required BenchmarkVolumeInfo Volume { get; init; }
    }

    /// <summary>Пункт выпадающего списка размеров тестового файла.</summary>
    public sealed class FileSizeOptionItem
    {
        public required string Label { get; init; }
        public required long Bytes { get; init; }
    }

    /// <summary>
    /// ViewModel вкладки «Бенчмарк». Логика перенесена из code-behind при
    /// MVVM-миграции (2026-08-26, одиннадцатая и последняя вкладка серии) без
    /// изменения поведения.
    /// Разбит на partial-файлы по образцу code-behind: .cs/.Disks.cs/.Run.cs/.Report.cs.
    /// </summary>
    public sealed partial class BenchmarkViewModel : ViewModelBase
    {
        // ── Внутреннее состояние (не биндится напрямую) ─────────────────────────

        private List<PhysicalDiskInfo> _disks = new();
        private PhysicalDiskInfo? _selectedDisk;
        private BenchmarkVolumeInfo? _selectedVolume;
        private List<string> _warnings = new();

        /// <summary>Номер последнего запроса предупреждений — защита от наложения вызовов.</summary>
        private int _warningsToken;

        private BenchmarkRunResult? _lastResult;
        private CancellationTokenSource? _cancellation;
        private bool _running;

        // ── Накопитель/том/размер файла ─────────────────────────────────────────

        private string _diskHintText = "Определение накопителей...";
        public string DiskHintText { get => _diskHintText; private set => SetField(ref _diskHintText, value); }

        private string _modelText = "—";
        public string ModelText { get => _modelText; private set => SetField(ref _modelText, value); }

        private string _capacityText = "—";
        public string CapacityText { get => _capacityText; private set => SetField(ref _capacityText, value); }

        private string _mediaText = "—";
        public string MediaText { get => _mediaText; private set => SetField(ref _mediaText, value); }

        private string _connectionText = "—";
        public string ConnectionText { get => _connectionText; private set => SetField(ref _connectionText, value); }

        private string _ceilingText = "—";
        public string CeilingText { get => _ceilingText; private set => SetField(ref _ceilingText, value); }

        private Brush _ceilingBrush = BrushResolver.Resolve("TextPrimary");
        public Brush CeilingBrush { get => _ceilingBrush; private set => SetField(ref _ceilingBrush, value); }

        private IReadOnlyList<DiskOptionItem> _diskOptions = Array.Empty<DiskOptionItem>();
        public IReadOnlyList<DiskOptionItem> DiskOptions { get => _diskOptions; private set => SetField(ref _diskOptions, value); }

        private DiskOptionItem? _selectedDiskOption;
        public DiskOptionItem? SelectedDiskOption
        {
            get => _selectedDiskOption;
            set
            {
                if (_selectedDiskOption == value) return;
                SetField(ref _selectedDiskOption, value);
                _selectedDisk = value?.Disk;
                ShowDiskDetails(_selectedDisk);
                FillVolumeOptions(_selectedDisk);
                _ = RefreshWarningsAsync();
            }
        }

        private IReadOnlyList<VolumeOptionItem> _volumeOptions = Array.Empty<VolumeOptionItem>();
        public IReadOnlyList<VolumeOptionItem> VolumeOptions { get => _volumeOptions; private set => SetField(ref _volumeOptions, value); }

        private VolumeOptionItem? _selectedVolumeOption;
        public VolumeOptionItem? SelectedVolumeOption
        {
            get => _selectedVolumeOption;
            set
            {
                if (_selectedVolumeOption == value) return;
                SetField(ref _selectedVolumeOption, value);
                _selectedVolume = value?.Volume;
                _ = RefreshWarningsAsync();
            }
        }

        private IReadOnlyList<FileSizeOptionItem> _fileSizeOptions = Array.Empty<FileSizeOptionItem>();
        public IReadOnlyList<FileSizeOptionItem> FileSizeOptions { get => _fileSizeOptions; private set => SetField(ref _fileSizeOptions, value); }

        private FileSizeOptionItem? _selectedFileSizeOption;
        public FileSizeOptionItem? SelectedFileSizeOption
        {
            get => _selectedFileSizeOption;
            set
            {
                if (_selectedFileSizeOption == value) return;
                SetField(ref _selectedFileSizeOption, value);
                _ = RefreshWarningsAsync();
            }
        }

        private string _profileTag = "Normal";
        public string ProfileTag { get => _profileTag; set => SetField(ref _profileTag, value); }

        private long SelectedFileSize => SelectedFileSizeOption?.Bytes ?? BenchmarkPresets.FileSizes[0];

        internal BenchmarkProfile SelectedProfile => ProfileTag switch
        {
            "Fast" => BenchmarkProfile.Fast,
            "Precise" => BenchmarkProfile.Precise,
            _ => BenchmarkProfile.Normal
        };

        // ── Предупреждения ───────────────────────────────────────────────────────

        private IReadOnlyList<string> _warningTexts = Array.Empty<string>();
        public IReadOnlyList<string> WarningTexts { get => _warningTexts; private set => SetField(ref _warningTexts, value); }

        private bool _showWarnings;
        public bool ShowWarnings { get => _showWarnings; private set => SetField(ref _showWarnings, value); }

        // ── Запуск/прогресс ──────────────────────────────────────────────────────

        private string _runButtonText = "▶ Запустить тест";
        public string RunButtonText { get => _runButtonText; private set => SetField(ref _runButtonText, value); }

        private string _runStatusText = "Тест ещё не запускался";
        public string RunStatusText { get => _runStatusText; private set => SetField(ref _runStatusText, value); }

        private bool _isRunEnabled = true;
        public bool IsRunEnabled { get => _isRunEnabled; private set => SetField(ref _isRunEnabled, value); }

        private bool _isControlsEnabled = true;
        public bool IsControlsEnabled { get => _isControlsEnabled; private set => SetField(ref _isControlsEnabled, value); }

        private bool _showProgress;
        public bool ShowProgress { get => _showProgress; private set => SetField(ref _showProgress, value); }

        private double _progressValue;
        public double ProgressValue { get => _progressValue; private set => SetField(ref _progressValue, value); }

        // ── Результаты / выводы ──────────────────────────────────────────────────

        private IReadOnlyList<BenchmarkResultRow> _resultRows;
        public IReadOnlyList<BenchmarkResultRow> ResultRows { get => _resultRows; private set => SetField(ref _resultRows, value); }

        private IReadOnlyList<ConclusionLine> _conclusionLines;
        public IReadOnlyList<ConclusionLine> ConclusionLines { get => _conclusionLines; private set => SetField(ref _conclusionLines, value); }

        private bool _isCopyReportEnabled;
        public bool IsCopyReportEnabled { get => _isCopyReportEnabled; private set => SetField(ref _isCopyReportEnabled, value); }

        private bool _isSaveReportEnabled;
        public bool IsSaveReportEnabled { get => _isSaveReportEnabled; private set => SetField(ref _isSaveReportEnabled, value); }

        // ── Команды ──────────────────────────────────────────────────────────────

        public RelayCommand RunBenchmarkCommand { get; }
        public RelayCommand CopyReportCommand { get; }
        public RelayCommand SaveReportCommand { get; }

        public BenchmarkViewModel()
        {
            _resultRows = BuildEmptyResultRows();
            _conclusionLines = new[]
            {
                new ConclusionLine { Text = "Запустите тест, чтобы увидеть разбор результата", Foreground = BrushResolver.Resolve("TextSecondary") }
            };

            RunBenchmarkCommand = RelayCommand.FromAsync(_ => RunBenchmarkAsync());
            CopyReportCommand   = new RelayCommand(_ => CopyReport());
            SaveReportCommand   = new RelayCommand(_ => SaveReport());
        }

        public async Task InitializeAsync()
        {
            FillFileSizeOptions();

            // Подчистка тестовых файлов, оставшихся от аварийно прерванных прогонов.
            // Через Task.Run, а не напрямую: метод вызывается до первого await, то есть
            // на UI-потоке, а внутри он опрашивает DriveInfo.IsReady по каждому тому —
            // на отключённом сетевом диске или пустом приводе это блокирует поток
            // на секунды, и вкладка открывается «зависшей».
            await Task.Run(DiskBenchmarkEngine.CleanupOrphanedFiles);

            await LoadDisksAsync();
        }
    }
}
