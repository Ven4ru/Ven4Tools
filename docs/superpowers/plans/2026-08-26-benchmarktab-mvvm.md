# BenchmarkTab MVVM Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Перенести логику вкладки «Бенчмарк» (`BenchmarkTab`, 607 строк в 4 partial-файлах code-behind) из code-behind в `BenchmarkViewModel`, оставив `BenchmarkTab.xaml`/`.xaml.cs` тонкой обёрткой. Одиннадцатая и ПОСЛЕДНЯЯ вкладка серии MVVM-миграции — после неё клиент Ven4Tools полностью на MVVM.

**Architecture:** `BenchmarkViewModel : INotifyPropertyChanged`, partial-класс по образцу code-behind — `BenchmarkViewModel.cs` (ядро + 5 вспомогательных типов) + `.Disks.cs` + `.Run.cs` + `.Report.cs`. Единственный TwoWay-риск во всей вкладке — `progressBenchmark.Value`. Программно построенная таблица результатов (4 строки) и списки предупреждений/выводов заменяются на `ItemsControl`+`DataTemplate`, с сохранением точной схемы `AutomationId` (`txtP{N}Read` и т.д.) через вычисляемые свойства на новом VM-типе `BenchmarkResultRow`.

**Tech Stack:** .NET 8, WPF, xUnit.

## Global Constraints

- Поведение 1:1 с оригиналом, кроме адаптаций:
  1. `DiskInventoryService`/`DiskBenchmarkEngine`/`BenchmarkWarningService`/`BenchmarkReportBuilder`/`BenchmarkPresets`/`AppLogger`/`MessageBox`/`Clipboard`/`SaveFileDialog` — из VM напрямую (устоявшийся паттерн). Никакие сервисные файлы не трогаются (в отличие от WindowsUpdateTab — здесь все модели/сервисы остаются как есть).
  2. Эта вкладка не использует ни `OwnerWindowProvider`, ни какие-либо события — самый простой внешний контракт серии (`MainWindow.xaml.cs:32,221-223`, без подписок).
  3. `_initialized` (защита повторного `Loaded`) остаётся в code-behind — WPF-lifecycle забота, не VM-концерн.
  4. Программно построенная таблица результатов (4 строки, `FindName($"txtP{i}{suffix}")`) заменяется на `ItemsControl ItemsSource="{Binding ResultRows}"` с `DataTemplate`. Новый тип `BenchmarkResultRow` несёт `Index` и 5 вычисляемых `*AutomationId`-свойств (`$"txtP{Index}Read"` и т.д.), биндящихся на `AutomationProperties.AutomationId` — точная замена статичных `x:Name`, необходимая для живого UI-теста `BenchmarkTabTests`, который ищет `txtP0Read` по AutomationId.
  5. Предупреждения (`pnlWarningItems.Children.Add(...)`) и выводы (`pnlConclusions.Children.Add(...)`) заменяются на `ItemsControl`+`DataTemplate` над `WarningTexts`/`ConclusionLines`.
  6. **Гонка двойного пересчёта предупреждений — переносится 1:1, НЕ оптимизируется.** `SelectedDiskOption`-сеттер вызывает `FillVolumeOptions(...)`, которая (когда есть подходящие тома) выставляет `SelectedVolumeOption` **через публичный сеттер** (не напрямую в поле) — это даёт вложенный вызов `RefreshWarningsAsync()`, ровно как оригинальное `cmbVolumes.SelectedIndex = preferred` внутри `FillVolumes` порождало реальное вложенное событие `CmbVolumes_SelectionChanged`. `SelectedDiskOption`-сеттер после `FillVolumeOptions(...)` **безусловно** ещё раз вызывает `RefreshWarningsAsync()`. Токен `_warningsToken` — обязательная защита от двойного результата, покрыта живым UI-тестом `Бенчмарк_ПредупрежденияНеДублируются`, не декоративна.
  7. `ShowDiskDetails`/`ClearResults`/`ShowResults`/`BuildEmptyResultRows` — сделаны `internal` (не `private`) как тестируемый seam, по аналогии с `internal`-методами в `DiagnosticsViewModel`/`WindowsUpdateViewModel` из прошлых миграций.
- **`progressBenchmark.Value` (через `RangeBase.Value`, TwoWay по умолчанию) → `ProgressValue` (`private set`) — ОБЯЗАТЕЛЬНО `Mode=OneWay`.** Единственный TwoWay-риск во всей вкладке — тот же класс бага, что уже трижды случался в серии (OfficeTab `29c2609`, DiagnosticsTab `9b3282f`, SystemTab `progressCache`).
- Все остальные биндинги TwoWay-по-умолчанию (`ComboBox.SelectedItem`×3, `ComboBox.SelectedValue`) идут на свойства с публичным сеттером и ссылочным/строковым типом — безопасны без специальных `Mode=`.
- `RunBenchmarkCommand` — **без `CanExecute`**: кнопка двухрежимная (Запустить/Остановить), гейт — прямой биндинг `IsRunEnabled` на `Button.IsEnabled` (как в оригинале единый `.IsEnabled` покрывал оба режима). `CanExecute`-гейт здесь заблокировал бы кнопку «Остановить» в момент, когда она обязана быть кликабельной.
- Все `x:Name`, участвующие в UI-тестах, сохраняются дословно: `btnBenchmarkTab` (MainWindow), `cmbDisks`, `txtConnection`, `cmbVolumes`, `txtCeiling`, `cmbProfile`, `btnRunBenchmark`, `btnCopyReport`, и вся схема `txtP{N}Name`/`txtP{N}Read`/`txtP{N}ReadSub`/`txtP{N}Write`/`txtP{N}WriteSub` для N=0..3 (через `AutomationProperties.AutomationId` на вычисляемых свойствах `BenchmarkResultRow`).
- Коммиты — на русском, без Claude/AI-атрибуции.
- Ветка `mvvm-benchmarktab` уже создана от `main`, спека закоммичена (`4a1157f`).

---

### Task 1: `BenchmarkViewModel` (4 файла) + юнит-тесты

**Files:**
- Create: `Ven4Tools/ViewModels/BenchmarkViewModel.cs`
- Create: `Ven4Tools/ViewModels/BenchmarkViewModel.Disks.cs`
- Create: `Ven4Tools/ViewModels/BenchmarkViewModel.Run.cs`
- Create: `Ven4Tools/ViewModels/BenchmarkViewModel.Report.cs`
- Test: `tests/Ven4Tools.Tests/BenchmarkViewModelTests.cs`

**Interfaces:**
- Consumes: `Ven4Tools.Services.DiskBenchmark.*` (не трогаем — `DiskInventoryService`/`DiskBenchmarkEngine`/`BenchmarkWarningService`/`BenchmarkReportBuilder`/`BenchmarkPresets`), `Ven4Tools.Models.*` (`PhysicalDiskInfo`/`BenchmarkVolumeInfo`/`BenchmarkRunResult`/`BenchmarkMeasurement`/`BenchmarkOperation`/`BenchmarkProfile`/`BenchmarkProgress`/`PciLinkInfo`), `Ven4Tools.Services.AppLogger`, `Ven4Tools.ViewModels.RelayCommand`/`RelayCommand.FromAsync`.
- Produces: `Ven4Tools.ViewModels.BenchmarkResultRow`/`ConclusionLine`/`DiskOptionItem`/`VolumeOptionItem`/`FileSizeOptionItem`, `Ven4Tools.ViewModels.BenchmarkViewModel` — все публичные свойства/команды (полный список — см. код ниже); публичный `Task InitializeAsync()`; `internal static Brush ResolveBrush(string)`; `internal void ShowDiskDetails(PhysicalDiskInfo?)`/`ClearResults()`/`ShowResults(BenchmarkRunResult)`.

- [ ] **Step 1: Создать `Ven4Tools/ViewModels/BenchmarkViewModel.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
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
    /// изменения поведения — см.
    /// docs/superpowers/specs/2026-08-26-benchmarktab-mvvm-design.md.
    /// Разбит на partial-файлы по образцу code-behind: .cs/.Disks.cs/.Run.cs/.Report.cs.
    /// </summary>
    public sealed partial class BenchmarkViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        internal static Brush ResolveBrush(string resourceKey) =>
            (Application.Current?.TryFindResource(resourceKey) as Brush) ?? Brushes.White;

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

        private Brush _ceilingBrush = ResolveBrush("TextPrimary");
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

        private BenchmarkProfile SelectedProfile => ProfileTag switch
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
                new ConclusionLine { Text = "Запустите тест, чтобы увидеть разбор результата", Foreground = ResolveBrush("TextSecondary") }
            };

            RunBenchmarkCommand = RelayCommand.FromAsync(_ => RunBenchmarkAsync());
            CopyReportCommand   = new RelayCommand(_ => CopyReport());
            SaveReportCommand   = new RelayCommand(_ => SaveReport());
        }

        public async Task InitializeAsync()
        {
            FillFileSizeOptions();

            // Подчистка тестовых файлов, оставшихся от аварийно прерванных прогонов.
            DiskBenchmarkEngine.CleanupOrphanedFiles();

            await LoadDisksAsync();
        }
    }
}
```

- [ ] **Step 2: Создать `Ven4Tools/ViewModels/BenchmarkViewModel.Disks.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.Services.DiskBenchmark;

namespace Ven4Tools.ViewModels
{
    public sealed partial class BenchmarkViewModel
    {
        private void FillFileSizeOptions()
        {
            var options = BenchmarkPresets.FileSizes
                .Select(size => new FileSizeOptionItem { Label = BenchmarkReportBuilder.FormatBinarySize(size), Bytes = size })
                .ToList();
            FileSizeOptions = options;
            SelectedFileSizeOption = options[0];
        }

        /// <summary>Заполняет список накопителей и выбирает первый пригодный для теста.</summary>
        private async Task LoadDisksAsync()
        {
            DiskHintText = "Определение накопителей...";
            DiskOptions = Array.Empty<DiskOptionItem>();

            try
            {
                _disks = await DiskInventoryService.GetDisksAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "BenchmarkViewModel.LoadDisksAsync");
                _disks.Clear();
            }

            var options = new List<DiskOptionItem>();
            foreach (var disk in _disks)
            {
                string capacity = disk.SizeBytes > 0
                    ? " — " + BenchmarkReportBuilder.FormatCapacity(disk.SizeBytes)
                    : "";
                string suffix = disk.CanBenchmark ? "" : " (нет тома для теста)";

                options.Add(new DiskOptionItem
                {
                    Label = $"Диск {disk.Index}: {disk.FriendlyName}{capacity}{suffix}",
                    Disk = disk,
                    CanBenchmark = disk.CanBenchmark
                });
            }
            DiskOptions = options;

            if (options.Count == 0)
            {
                DiskHintText = "Накопители не обнаружены. Подробности — в журнале приложения.";
                IsRunEnabled = false;
                return;
            }

            // Выбираем первый накопитель, на котором есть куда положить тестовый файл.
            int selectIndex = -1;
            for (int index = 0; index < _disks.Count; index++)
            {
                if (_disks[index].CanBenchmark) { selectIndex = index; break; }
            }

            if (selectIndex >= 0)
            {
                SelectedDiskOption = options[selectIndex];
            }
            else
            {
                DiskHintText = "Ни на одном накопителе нет тома, пригодного для теста.";
                IsRunEnabled = false;
            }
        }

        internal void ShowDiskDetails(PhysicalDiskInfo? disk)
        {
            if (disk == null)
            {
                ModelText = "—";
                CapacityText = "—";
                MediaText = "—";
                ConnectionText = "—";
                CeilingText = "—";
                return;
            }

            ModelText = disk.FriendlyName;
            CapacityText = disk.SizeBytes > 0
                ? BenchmarkReportBuilder.FormatCapacity(disk.SizeBytes)
                : "неизвестно";

            MediaText = BenchmarkReportBuilder.DescribeMediaWithSpindle(disk);
            ConnectionText = BenchmarkReportBuilder.DescribeConnection(disk);

            // Потолок показываем только когда параметры линии получены достоверно.
            if (disk.Link.IsKnown && disk.Link.CeilingMegabytesPerSecond > 0)
            {
                CeilingText = BenchmarkReportBuilder.FormatSpeedRounded(disk.Link.CeilingMegabytesPerSecond) + " МБ/с";
                CeilingBrush = ResolveBrush("TextPrimary");
            }
            else
            {
                CeilingText = "неизвестно — параметры интерфейса недоступны";
                CeilingBrush = ResolveBrush("TextSecondary");
            }
        }

        /// <summary>
        /// Пересобирает список томов текущего накопителя и, если есть подходящие,
        /// автоматически выбирает предпочтительный ЧЕРЕЗ ПУБЛИЧНЫЙ СЕТТЕР
        /// SelectedVolumeOption — ровно как оригинал переставлял cmbVolumes.SelectedIndex,
        /// что запускало реальное событие выбора и вложенный вызов RefreshWarningsAsync.
        /// Сброс в начале — напрямую в поле, без побочного эффекта: соответствует
        /// оригинальному "_selectedVolume = null;", не связанному с UI-событием.
        /// </summary>
        private void FillVolumeOptions(PhysicalDiskInfo? disk)
        {
            SetField(ref _selectedVolumeOption, null, nameof(SelectedVolumeOption));
            _selectedVolume = null;

            var options = new List<VolumeOptionItem>();

            if (disk == null)
            {
                DiskHintText = "Накопитель не выбран.";
                VolumeOptions = options;
                return;
            }

            foreach (var volume in disk.Volumes)
            {
                if (!volume.IsReady) continue;

                string label = string.IsNullOrWhiteSpace(volume.Label) ? "" : $" «{volume.Label}»";
                string system = volume.IsSystem ? ", системный" : "";
                options.Add(new VolumeOptionItem
                {
                    Label = $"{volume.Letter}{label} — свободно " +
                            BenchmarkReportBuilder.FormatCapacity(volume.FreeBytes) + system,
                    Volume = volume
                });
            }

            VolumeOptions = options;

            if (options.Count == 0)
            {
                DiskHintText = "На этом накопителе нет тома, пригодного для теста. " +
                                "Тест выполняется через файл, поэтому нужен размеченный том с файловой системой.";
                IsRunEnabled = false;
                return;
            }

            // Несистемный том предпочтительнее: на нём меньше постороннего фона.
            int preferred = 0;
            for (int index = 0; index < options.Count; index++)
            {
                if (!options[index].Volume.IsSystem) { preferred = index; break; }
            }

            DiskHintText = "Тест измеряет скорость выбранного накопителя через временный файл на указанном томе.";
            SelectedVolumeOption = options[preferred];
        }

        /// <summary>
        /// Пересобирает список предупреждений и решает, можно ли запускать тест.
        ///
        /// Выбор накопителя перевыставляет и том, поэтому метод легко вызывается дважды
        /// внахлёст. Токен гарантирует, что панель заполнит только последний вызов: без него
        /// два параллельных прохода дописывали в неё одни и те же предупреждения дважды.
        /// </summary>
        private async Task RefreshWarningsAsync()
        {
            int token = ++_warningsToken;

            if (_selectedVolume == null)
            {
                WarningTexts = Array.Empty<string>();
                _warnings.Clear();
                ShowWarnings = false;
                IsRunEnabled = false;
                return;
            }

            var volume = _selectedVolume;
            bool allowed = BenchmarkWarningService.TryValidateFreeSpace(
                volume, SelectedFileSize, out string blockingError);

            // Опрос состояния шифрования тома занимает у Windows несколько секунд, поэтому
            // объясняем пользователю, почему кнопка запуска пока недоступна.
            if (!_running) RunStatusText = "Проверяем том...";

            var collected = new List<string>();
            try
            {
                collected = await BenchmarkWarningService.CollectAsync(volume);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "BenchmarkViewModel.RefreshWarningsAsync");
            }

            // Пока шёл опрос тома, пользователь мог переключить накопитель — тогда этот
            // результат уже неактуален и трогать интерфейс не должен.
            if (token != _warningsToken) return;

            if (!allowed) collected.Insert(0, blockingError);
            _warnings = collected;

            WarningTexts = _warnings.Select(w => "• " + w).ToList();
            ShowWarnings = _warnings.Count > 0;
            IsRunEnabled = allowed && !_running;

            if (!_running)
            {
                RunStatusText = allowed
                    ? "Тест ещё не запускался"
                    : "Запуск невозможен — смотрите предупреждение выше";
            }
        }
    }
}
```

- [ ] **Step 3: Создать `Ven4Tools/ViewModels/BenchmarkViewModel.Run.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.Services.DiskBenchmark;

namespace Ven4Tools.ViewModels
{
    public sealed partial class BenchmarkViewModel
    {
        private async Task RunBenchmarkAsync()
        {
            // Во время прогона та же кнопка останавливает тест.
            if (_running)
            {
                _cancellation?.Cancel();
                IsRunEnabled = false;
                RunStatusText = "Останавливаем...";
                return;
            }

            if (_selectedDisk == null || _selectedVolume == null) return;

            if (!BenchmarkWarningService.TryValidateFreeSpace(
                    _selectedVolume, SelectedFileSize, out string blockingError))
            {
                MessageBox.Show(blockingError, "Тест не запущен",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _running = true;
            _lastResult = null;
            _cancellation = new CancellationTokenSource();

            RunButtonText = "⏹ Остановить";
            IsCopyReportEnabled = false;
            IsSaveReportEnabled = false;
            IsControlsEnabled = false;
            ShowProgress = true;
            ProgressValue = 0;
            ClearResults();

            var progress = new Progress<BenchmarkProgress>(report =>
            {
                RunStatusText = report.Stage;
                ProgressValue = Math.Max(0, Math.Min(100, report.Fraction * 100));
            });

            AppLogger.Write($"⏱️ Запущен тест скорости диска: {_selectedDisk.FriendlyName}, " +
                            $"том {_selectedVolume.Letter}, профиль {BenchmarkPresets.DescribeProfile(SelectedProfile)}");

            try
            {
                var result = await DiskBenchmarkEngine.RunAsync(
                    _selectedDisk, _selectedVolume, SelectedProfile, SelectedFileSize,
                    progress, _cancellation.Token);

                foreach (string warning in _warnings) result.Warnings.Add(warning);

                _lastResult = result;
                ShowResults(result);

                RunStatusText = result.Cancelled
                    ? "Тест остановлен, показаны частичные результаты"
                    : "Готово за " + BenchmarkReportBuilder.FormatDuration(result.Duration);

                IsCopyReportEnabled = result.Measurements.Count > 0;
                IsSaveReportEnabled = result.Measurements.Count > 0;

                AppLogger.Write(result.Cancelled
                    ? "⏹️ Тест скорости диска остановлен пользователем"
                    : "✅ Тест скорости диска завершён");
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "BenchmarkViewModel.RunBenchmarkAsync");
                RunStatusText = "Не удалось выполнить тест";
                MessageBox.Show(
                    "Не удалось выполнить тест: " + ex.Message +
                    "\n\nПодробности сохранены в журнале приложения.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _running = false;
                _cancellation?.Dispose();
                _cancellation = null;

                RunButtonText = "▶ Запустить тест";
                IsRunEnabled = true;
                IsControlsEnabled = true;
                ShowProgress = false;
            }
        }

        private List<BenchmarkResultRow> BuildEmptyResultRows()
        {
            var rows = new List<BenchmarkResultRow>();
            for (int index = 0; index < BenchmarkPresets.Patterns.Length; index++)
            {
                rows.Add(new BenchmarkResultRow
                {
                    Index = index,
                    Name = BenchmarkPresets.Patterns[index].Name,
                    ReadValueText = "—",
                    ReadSubText = "",
                    WriteValueText = "—",
                    WriteSubText = ""
                });
            }
            return rows;
        }

        internal void ClearResults()
        {
            ResultRows = BuildEmptyResultRows();
            ConclusionLines = new[]
            {
                new ConclusionLine { Text = "Идёт измерение...", Foreground = ResolveBrush("TextSecondary") }
            };
        }

        internal void ShowResults(BenchmarkRunResult result)
        {
            var rows = new List<BenchmarkResultRow>();
            for (int index = 0; index < BenchmarkPresets.Patterns.Length; index++)
            {
                string name = BenchmarkPresets.Patterns[index].Name;
                var read = result.Find(name, BenchmarkOperation.Read);
                var write = result.Find(name, BenchmarkOperation.Write);
                rows.Add(new BenchmarkResultRow
                {
                    Index = index,
                    Name = name,
                    ReadValueText = FormatCellValue(read),
                    ReadSubText = FormatCellSub(read),
                    WriteValueText = FormatCellValue(write),
                    WriteSubText = FormatCellSub(write)
                });
            }
            ResultRows = rows;

            ShowConclusions(result);
        }

        private static string FormatCellValue(BenchmarkMeasurement? measurement) =>
            measurement == null ? "—" : BenchmarkReportBuilder.FormatSpeed(measurement.MegabytesPerSecond) + " МБ/с";

        private static string FormatCellSub(BenchmarkMeasurement? measurement) =>
            measurement == null ? "" : BenchmarkReportBuilder.FormatIops(measurement.OperationsPerSecond) +
                                        " оп/с · задержка " +
                                        BenchmarkReportBuilder.FormatLatency(measurement.AverageLatencyMicroseconds);

        private void ShowConclusions(BenchmarkRunResult result)
        {
            var lines = new List<ConclusionLine>();

            if (result.Measurements.Count == 0)
            {
                lines.Add(MakeConclusion("Замеры не выполнены."));
                ConclusionLines = lines;
                return;
            }

            if (result.Cancelled)
                lines.Add(MakeConclusion("Тест остановлен досрочно — показаны только успевшие завершиться замеры."));

            var sequentialRead = result.Find("SEQ1M Q8T1", BenchmarkOperation.Read);
            if (sequentialRead != null)
            {
                lines.Add(MakeConclusion("Последовательное чтение " +
                              BenchmarkReportBuilder.FormatSpeedRounded(sequentialRead.MegabytesPerSecond) +
                              " МБ/с — " +
                              BenchmarkReportBuilder.DescribeLevel(sequentialRead.MegabytesPerSecond) + "."));

                var disk = result.Disk;
                if (disk != null && disk.Link.IsKnown && disk.Link.CeilingMegabytesPerSecond > 0)
                {
                    double share = sequentialRead.MegabytesPerSecond / disk.Link.CeilingMegabytesPerSecond * 100;
                    lines.Add(MakeConclusion("Накопитель выбирает " + BenchmarkReportBuilder.FormatPercent(share) +
                                  "% пропускной способности интерфейса (" +
                                  BenchmarkReportBuilder.FormatSpeedRounded(disk.Link.CeilingMegabytesPerSecond) +
                                  " МБ/с)."));
                }
                else
                {
                    lines.Add(MakeConclusion("Потолок интерфейса не определён, поэтому долю его использования " +
                                  "показать нельзя — приблизительное значение здесь только вводило бы в заблуждение."));
                }
            }

            var randomRead = result.Find("RND4K Q1T1", BenchmarkOperation.Read);
            if (randomRead != null && randomRead.AverageLatencyMicroseconds > 0)
            {
                lines.Add(MakeConclusion("Задержка одиночного случайного чтения — " +
                              BenchmarkReportBuilder.FormatLatency(randomRead.AverageLatencyMicroseconds) +
                              ". Отзывчивость системы и скорость запуска программ зависят от неё " +
                              "сильнее, чем от последовательной скорости."));
            }

            ConclusionLines = lines;
        }

        private static ConclusionLine MakeConclusion(string text) =>
            new() { Text = "• " + text, Foreground = ResolveBrush("TextPrimary") };
    }
}
```

- [ ] **Step 4: Создать `Ven4Tools/ViewModels/BenchmarkViewModel.Report.cs`**

```csharp
using System;
using System.IO;
using System.Text;
using System.Windows;
using Ven4Tools.Services;
using Ven4Tools.Services.DiskBenchmark;

namespace Ven4Tools.ViewModels
{
    public sealed partial class BenchmarkViewModel
    {
        private void CopyReport()
        {
            if (_lastResult == null) return;

            try
            {
                Clipboard.SetText(BenchmarkReportBuilder.Build(_lastResult));
                AppLogger.Write("📤 Отчёт теста скорости диска скопирован в буфер обмена");
                MessageBox.Show("Отчёт скопирован в буфер обмена.", "Готово",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "BenchmarkViewModel.CopyReport");
                MessageBox.Show("Не удалось скопировать отчёт: " + ex.Message, "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveReport()
        {
            if (_lastResult == null) return;

            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Сохранить отчёт теста скорости диска",
                    Filter = "Текстовый файл (*.txt)|*.txt",
                    DefaultExt = ".txt",
                    FileName = $"Ven4Tools_тест_диска_{DateTime.Now:yyyy-MM-dd_HH-mm}.txt"
                };

                if (dialog.ShowDialog() != true) return;

                File.WriteAllText(dialog.FileName, BenchmarkReportBuilder.Build(_lastResult), Encoding.UTF8);
                AppLogger.Write("💾 Отчёт теста скорости диска сохранён в файл");
                MessageBox.Show("Отчёт сохранён.", "Готово",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "BenchmarkViewModel.SaveReport");
                MessageBox.Show("Не удалось сохранить отчёт: " + ex.Message, "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
```

- [ ] **Step 5: Написать `tests/Ven4Tools.Tests/BenchmarkViewModelTests.cs`**

```csharp
using Ven4Tools.Models;
using Ven4Tools.Services.DiskBenchmark;
using Ven4Tools.ViewModels;
using Xunit;

namespace Ven4Tools.Tests
{
    public class BenchmarkViewModelTests
    {
        [Fact]
        public void Конструктор_УстанавливаетДефолты()
        {
            var vm = new BenchmarkViewModel();

            Assert.Equal("Определение накопителей...", vm.DiskHintText);
            Assert.Equal("—", vm.ModelText);
            Assert.Equal("—", vm.CapacityText);
            Assert.Equal("—", vm.MediaText);
            Assert.Equal("—", vm.ConnectionText);
            Assert.Equal("—", vm.CeilingText);
            Assert.Empty(vm.DiskOptions);
            Assert.Empty(vm.VolumeOptions);
            Assert.Empty(vm.FileSizeOptions);
            Assert.Equal("Normal", vm.ProfileTag);
            Assert.Empty(vm.WarningTexts);
            Assert.False(vm.ShowWarnings);
            Assert.Equal("▶ Запустить тест", vm.RunButtonText);
            Assert.Equal("Тест ещё не запускался", vm.RunStatusText);
            Assert.True(vm.IsRunEnabled);
            Assert.True(vm.IsControlsEnabled);
            Assert.False(vm.ShowProgress);
            Assert.Equal(0, vm.ProgressValue);
            Assert.False(vm.IsCopyReportEnabled);
            Assert.False(vm.IsSaveReportEnabled);
        }

        [Fact]
        public void Конструктор_УстанавливаетПустыеСтрокиРезультатов()
        {
            var vm = new BenchmarkViewModel();

            Assert.Equal(BenchmarkPresets.Patterns.Length, vm.ResultRows.Count);
            for (int i = 0; i < vm.ResultRows.Count; i++)
            {
                Assert.Equal(BenchmarkPresets.Patterns[i].Name, vm.ResultRows[i].Name);
                Assert.Equal("—", vm.ResultRows[i].ReadValueText);
                Assert.Equal("", vm.ResultRows[i].ReadSubText);
                Assert.Equal("—", vm.ResultRows[i].WriteValueText);
                Assert.Equal("", vm.ResultRows[i].WriteSubText);
            }
        }

        [Fact]
        public void Конструктор_УстанавливаетПлейсхолдерВыводов()
        {
            var vm = new BenchmarkViewModel();

            Assert.Single(vm.ConclusionLines);
            Assert.Equal("Запустите тест, чтобы увидеть разбор результата", vm.ConclusionLines[0].Text);
        }

        [Fact]
        public void RunBenchmarkCommand_CanExecute_ВсегдаTrue()
        {
            var vm = new BenchmarkViewModel();
            Assert.True(vm.RunBenchmarkCommand.CanExecute(null));
        }

        [Fact]
        public void ProfileTag_Изменение_ПоднимаетPropertyChanged()
        {
            var vm = new BenchmarkViewModel();
            bool raised = false;
            vm.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(BenchmarkViewModel.ProfileTag);

            vm.ProfileTag = "Fast";

            Assert.Equal("Fast", vm.ProfileTag);
            Assert.True(raised);
        }

        [Fact]
        public void BenchmarkResultRow_AutomationId_ВычисляетсяПоIndex()
        {
            var row = new BenchmarkResultRow
            {
                Index = 2,
                Name = "RND4K Q32T16",
                ReadValueText = "—", ReadSubText = "", WriteValueText = "—", WriteSubText = ""
            };

            Assert.Equal("txtP2Name", row.NameAutomationId);
            Assert.Equal("txtP2Read", row.ReadAutomationId);
            Assert.Equal("txtP2ReadSub", row.ReadSubAutomationId);
            Assert.Equal("txtP2Write", row.WriteAutomationId);
            Assert.Equal("txtP2WriteSub", row.WriteSubAutomationId);
        }

        [Fact]
        public void ShowDiskDetails_СДиском_ЗаполняетТексты()
        {
            var vm = new BenchmarkViewModel();
            var disk = new PhysicalDiskInfo
            {
                Index = 0,
                FriendlyName = "Тестовый SSD",
                SizeBytes = 512L * 1024 * 1024 * 1024,
                Bus = DiskBusKind.Nvme,
                Media = DiskMediaKind.Ssd,
                Link = new PciLinkInfo { Generation = 4, Width = 4 }
            };

            vm.ShowDiskDetails(disk);

            Assert.Equal("Тестовый SSD", vm.ModelText);
            Assert.NotEqual("—", vm.CapacityText);
            Assert.NotEqual("—", vm.ConnectionText);
            Assert.EndsWith("МБ/с", vm.CeilingText);
        }

        [Fact]
        public void ShowDiskDetails_БезДиска_СбрасываетТекстыВПрочерк()
        {
            var vm = new BenchmarkViewModel();

            vm.ShowDiskDetails(null);

            Assert.Equal("—", vm.ModelText);
            Assert.Equal("—", vm.CapacityText);
            Assert.Equal("—", vm.MediaText);
            Assert.Equal("—", vm.ConnectionText);
            Assert.Equal("—", vm.CeilingText);
        }

        [Fact]
        public void ShowDiskDetails_НеизвестныйПотолок_ПоказываетЧестноеНеизвестно()
        {
            var vm = new BenchmarkViewModel();
            var disk = new PhysicalDiskInfo
            {
                Index = 0,
                FriendlyName = "Диск без известной линии",
                SizeBytes = 1024L * 1024 * 1024,
                Link = PciLinkInfo.Unknown
            };

            vm.ShowDiskDetails(disk);

            Assert.Contains("неизвестно", vm.CeilingText);
        }

        [Fact]
        public void ClearResults_ЗаполняетЗаглушкиИСтатусИзмерения()
        {
            var vm = new BenchmarkViewModel();

            vm.ClearResults();

            Assert.Equal(BenchmarkPresets.Patterns.Length, vm.ResultRows.Count);
            Assert.All(vm.ResultRows, row => Assert.Equal("—", row.ReadValueText));
            Assert.Single(vm.ConclusionLines);
            Assert.Equal("Идёт измерение...", vm.ConclusionLines[0].Text);
        }

        [Fact]
        public void ShowResults_СИзмерениями_ЗаполняетСтрокуИВыводы()
        {
            var vm = new BenchmarkViewModel();
            var pattern = BenchmarkPresets.Patterns[0];
            var result = new BenchmarkRunResult
            {
                Profile = BenchmarkProfile.Fast,
                Passes = 1,
                FileSizeBytes = 1024
            };
            result.Measurements.Add(new BenchmarkMeasurement
            {
                PatternName = pattern.Name,
                Operation = BenchmarkOperation.Read,
                MegabytesPerSecond = 500,
                OperationsPerSecond = 1000,
                AverageLatencyMicroseconds = 50
            });

            vm.ShowResults(result);

            Assert.Contains("МБ/с", vm.ResultRows[0].ReadValueText);
            Assert.NotEqual("—", vm.ResultRows[0].ReadValueText);
            Assert.NotEmpty(vm.ConclusionLines);
        }

        [Fact]
        public void ShowResults_БезИзмерений_ПоказываетЗамерыНеВыполнены()
        {
            var vm = new BenchmarkViewModel();
            var result = new BenchmarkRunResult { Profile = BenchmarkProfile.Fast, Passes = 1, FileSizeBytes = 1024 };

            vm.ShowResults(result);

            Assert.Single(vm.ConclusionLines);
            Assert.Equal("• Замеры не выполнены.", vm.ConclusionLines[0].Text);
        }
    }
}
```

- [ ] **Step 6: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок, 0 предупреждений.

- [ ] **Step 7: Прогнать новые тесты**

Run: `dotnet test tests/Ven4Tools.Tests -c Release --filter "FullyQualifiedName~BenchmarkViewModelTests"`
Expected: все новые тесты зелёные.

- [ ] **Step 8: Commit**

```bash
git add Ven4Tools/ViewModels/BenchmarkViewModel.cs Ven4Tools/ViewModels/BenchmarkViewModel.Disks.cs Ven4Tools/ViewModels/BenchmarkViewModel.Run.cs Ven4Tools/ViewModels/BenchmarkViewModel.Report.cs tests/Ven4Tools.Tests/BenchmarkViewModelTests.cs
git commit -m "feat(benchmark): BenchmarkViewModel (4 файла) + юнит-тесты"
```

---

### Task 2: Переписать `BenchmarkTab.xaml`/`.xaml.cs` на тонкую обёртку

**Files:**
- Modify: `Ven4Tools/Views/Tabs/BenchmarkTab.xaml`
- Modify: `Ven4Tools/Views/Tabs/BenchmarkTab.xaml.cs`
- Delete: `Ven4Tools/Views/Tabs/BenchmarkTab.Disks.cs`
- Delete: `Ven4Tools/Views/Tabs/BenchmarkTab.Run.cs`
- Delete: `Ven4Tools/Views/Tabs/BenchmarkTab.Report.cs`

**Interfaces:**
- Consumes: `Ven4Tools.ViewModels.BenchmarkViewModel` (Task 1) — вся публичная поверхность.
- Produces: `BenchmarkTab` — публичной поверхности сверх конструктора нет (внешний контракт `MainWindow.xaml.cs:221-223` — `new BenchmarkTab()` без подписок).

- [ ] **Step 1: Переписать `Ven4Tools/Views/Tabs/BenchmarkTab.xaml`**

Полное содержимое файла:

```xml
<UserControl x:Class="Ven4Tools.Views.Tabs.BenchmarkTab"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="{DynamicResource ContentBackground}">
    <UserControl.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis"/>
    </UserControl.Resources>
    <Grid Margin="20">
        <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
        <StackPanel Margin="0,0,0,16">
            <TextBlock Text="Тест скорости диска" Style="{StaticResource PageTitleStyle}"/>
            <TextBlock Text="Измерение скорости накопителя, тип подключения и потолок интерфейса. Работает без интернета"
                       Foreground="{DynamicResource TextSecondary}" Margin="0,4,0,0"/>
        </StackPanel>

        <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto">
            <StackPanel Margin="8,0,8,8">

                <!-- Выбор накопителя и тома -->
                <GroupBox Header="💽 Что тестируем" Margin="0,0,0,15">
                    <StackPanel Margin="10">
                        <Grid Margin="0,5">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="150"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                            <TextBlock Text="Накопитель:" Foreground="{DynamicResource TextSecondary}" VerticalAlignment="Center"/>
                            <ComboBox x:Name="cmbDisks" Grid.Column="1" Height="30"
                                      ItemsSource="{Binding DiskOptions}" DisplayMemberPath="Label"
                                      SelectedItem="{Binding SelectedDiskOption, Mode=TwoWay}"
                                      IsEnabled="{Binding IsControlsEnabled}">
                                <ComboBox.ItemContainerStyle>
                                    <Style TargetType="ComboBoxItem">
                                        <Setter Property="IsEnabled" Value="{Binding CanBenchmark}"/>
                                    </Style>
                                </ComboBox.ItemContainerStyle>
                            </ComboBox>
                        </Grid>
                        <Grid Margin="0,10,0,5">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="150"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                            <TextBlock Text="Том для теста:" Foreground="{DynamicResource TextSecondary}" VerticalAlignment="Center"/>
                            <ComboBox x:Name="cmbVolumes" Grid.Column="1" Height="30"
                                      ItemsSource="{Binding VolumeOptions}" DisplayMemberPath="Label"
                                      SelectedItem="{Binding SelectedVolumeOption, Mode=TwoWay}"
                                      IsEnabled="{Binding IsControlsEnabled}"/>
                        </Grid>
                        <TextBlock x:Name="txtDiskHint" Margin="0,10,0,0" TextWrapping="Wrap"
                                   Foreground="{DynamicResource TextSecondary}" FontSize="11"
                                   Text="{Binding DiskHintText}"/>
                    </StackPanel>
                </GroupBox>

                <!-- Сведения о накопителе -->
                <GroupBox Header="🔌 Подключение накопителя" Margin="0,0,0,15">
                    <StackPanel Margin="10">
                        <Grid Margin="0,4">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="150"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                            <TextBlock Text="Модель:" Foreground="{DynamicResource TextSecondary}"/>
                            <TextBlock x:Name="txtModel" Grid.Column="1" Text="{Binding ModelText}" Foreground="{DynamicResource TextPrimary}" TextWrapping="Wrap"/>
                        </Grid>
                        <Grid Margin="0,4">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="150"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                            <TextBlock Text="Объём:" Foreground="{DynamicResource TextSecondary}"/>
                            <TextBlock x:Name="txtCapacity" Grid.Column="1" Text="{Binding CapacityText}" Foreground="{DynamicResource TextPrimary}"/>
                        </Grid>
                        <Grid Margin="0,4">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="150"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                            <TextBlock Text="Тип носителя:" Foreground="{DynamicResource TextSecondary}"/>
                            <TextBlock x:Name="txtMedia" Grid.Column="1" Text="{Binding MediaText}" Foreground="{DynamicResource TextPrimary}"/>
                        </Grid>
                        <Grid Margin="0,4">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="150"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                            <TextBlock Text="Подключение:" Foreground="{DynamicResource TextSecondary}"/>
                            <TextBlock x:Name="txtConnection" Grid.Column="1" Text="{Binding ConnectionText}" Foreground="{DynamicResource TextPrimary}" TextWrapping="Wrap"/>
                        </Grid>
                        <Grid Margin="0,4">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="150"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                            <TextBlock Text="Потолок интерфейса:" Foreground="{DynamicResource TextSecondary}"/>
                            <TextBlock x:Name="txtCeiling" Grid.Column="1" Text="{Binding CeilingText}" Foreground="{Binding CeilingBrush}"/>
                        </Grid>
                        <TextBlock Margin="0,10,0,0" TextWrapping="Wrap" FontSize="11"
                                   Foreground="{DynamicResource TextSecondary}"
                                   Text="ℹ️ Точный слот на материнской плате определить нельзя — Windows такую разметку не сообщает. Всё, что не удалось выяснить достоверно, помечается как «не определяется», а не подставляется приблизительно."/>
                    </StackPanel>
                </GroupBox>

                <!-- Параметры прогона -->
                <GroupBox Header="⚙️ Параметры теста" Margin="0,0,0,15">
                    <StackPanel Margin="10">
                        <Grid Margin="0,5">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="150"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                            <TextBlock Text="Профиль:" Foreground="{DynamicResource TextSecondary}" VerticalAlignment="Center"/>
                            <ComboBox x:Name="cmbProfile" Grid.Column="1" Height="30"
                                      SelectedValuePath="Tag" SelectedValue="{Binding ProfileTag, Mode=TwoWay}"
                                      IsEnabled="{Binding IsControlsEnabled}">
                                <ComboBoxItem Content="Быстрый — 1 проход, около минуты" Tag="Fast"/>
                                <ComboBoxItem Content="Обычный — 3 прохода, около трёх минут" Tag="Normal"/>
                                <ComboBoxItem Content="Точный — 5 проходов, около пяти минут" Tag="Precise"/>
                            </ComboBox>
                        </Grid>
                        <Grid Margin="0,10,0,5">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="150"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                            <TextBlock Text="Тестовый файл:" Foreground="{DynamicResource TextSecondary}" VerticalAlignment="Center"/>
                            <ComboBox x:Name="cmbFileSize" Grid.Column="1" Height="30"
                                      ItemsSource="{Binding FileSizeOptions}" DisplayMemberPath="Label"
                                      SelectedItem="{Binding SelectedFileSizeOption, Mode=TwoWay}"
                                      IsEnabled="{Binding IsControlsEnabled}"/>
                        </Grid>
                        <TextBlock Margin="0,10,0,0" TextWrapping="Wrap" FontSize="11"
                                   Foreground="{DynamicResource TextSecondary}"
                                   Text="ℹ️ Тест работает через временный файл на выбранном томе и удаляет его после прогона. Прямой записи на устройство в обход файловой системы нет — данные в безопасности."/>
                    </StackPanel>
                </GroupBox>

                <!-- Предупреждения -->
                <Border x:Name="pnlWarnings" Background="{DynamicResource CardBackground}" CornerRadius="6"
                        Padding="14" Margin="0,0,0,15"
                        Visibility="{Binding ShowWarnings, Converter={StaticResource BoolToVis}}"
                        BorderThickness="1" BorderBrush="{DynamicResource StatusWarning}">
                    <StackPanel>
                        <TextBlock Text="⚠️ Что может повлиять на результат" FontWeight="SemiBold"
                                   Foreground="{DynamicResource StatusWarning}" Margin="0,0,0,8"/>
                        <ItemsControl x:Name="pnlWarningItems" ItemsSource="{Binding WarningTexts}">
                            <ItemsControl.ItemTemplate>
                                <DataTemplate>
                                    <TextBlock Text="{Binding}" TextWrapping="Wrap" Margin="0,0,0,6" Foreground="{DynamicResource TextPrimary}"/>
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>
                    </StackPanel>
                </Border>

                <!-- Запуск -->
                <Border Background="{DynamicResource CardBackground}" CornerRadius="6" Padding="14" Margin="0,0,0,15">
                    <StackPanel>
                        <StackPanel Orientation="Horizontal">
                            <Button x:Name="btnRunBenchmark" Content="{Binding RunButtonText}" Height="36" Width="200"
                                    ToolTip="Создаст временный файл на выбранном томе и измерит чтение и запись. Тест может занять несколько минут."
                                    IsEnabled="{Binding IsRunEnabled}"
                                    Command="{Binding RunBenchmarkCommand}"/>
                            <TextBlock x:Name="txtRunStatus" VerticalAlignment="Center" Margin="14,0,0,0"
                                       Foreground="{DynamicResource TextSecondary}"
                                       Text="{Binding RunStatusText}"/>
                        </StackPanel>
                        <ProgressBar x:Name="progressBenchmark" Height="6" Minimum="0" Maximum="100"
                                     Margin="0,12,0,0"
                                     Value="{Binding ProgressValue, Mode=OneWay}"
                                     Visibility="{Binding ShowProgress, Converter={StaticResource BoolToVis}}"/>
                    </StackPanel>
                </Border>

                <!-- Результаты -->
                <GroupBox Header="📊 Результаты" Margin="0,0,0,15">
                    <StackPanel Margin="10">
                        <Grid Margin="0,0,0,8">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="150"/>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="*"/>
                            </Grid.ColumnDefinitions>
                            <TextBlock Grid.Column="0" Text="Тест" FontWeight="SemiBold" Foreground="{DynamicResource TextSecondary}"/>
                            <TextBlock Grid.Column="1" Text="Чтение" FontWeight="SemiBold" Foreground="{DynamicResource TextSecondary}"/>
                            <TextBlock Grid.Column="2" Text="Запись" FontWeight="SemiBold" Foreground="{DynamicResource TextSecondary}"/>
                        </Grid>
                        <ItemsControl x:Name="gridResults" ItemsSource="{Binding ResultRows}">
                            <ItemsControl.ItemTemplate>
                                <DataTemplate>
                                    <Grid>
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="150"/>
                                            <ColumnDefinition Width="*"/>
                                            <ColumnDefinition Width="*"/>
                                        </Grid.ColumnDefinitions>
                                        <TextBlock Grid.Column="0" Text="{Binding Name}"
                                                   AutomationProperties.AutomationId="{Binding NameAutomationId}"
                                                   Foreground="{DynamicResource TextPrimary}" VerticalAlignment="Center"/>
                                        <StackPanel Grid.Column="1" Margin="0,6">
                                            <TextBlock Text="{Binding ReadValueText}"
                                                       AutomationProperties.AutomationId="{Binding ReadAutomationId}"
                                                       FontSize="20" FontWeight="SemiBold" Foreground="{DynamicResource AccentColor}"/>
                                            <TextBlock Text="{Binding ReadSubText}"
                                                       AutomationProperties.AutomationId="{Binding ReadSubAutomationId}"
                                                       FontSize="10" Foreground="{DynamicResource TextSecondary}"/>
                                        </StackPanel>
                                        <StackPanel Grid.Column="2" Margin="0,6">
                                            <TextBlock Text="{Binding WriteValueText}"
                                                       AutomationProperties.AutomationId="{Binding WriteAutomationId}"
                                                       FontSize="20" FontWeight="SemiBold" Foreground="{DynamicResource TextPrimary}"/>
                                            <TextBlock Text="{Binding WriteSubText}"
                                                       AutomationProperties.AutomationId="{Binding WriteSubAutomationId}"
                                                       FontSize="10" Foreground="{DynamicResource TextSecondary}"/>
                                        </StackPanel>
                                    </Grid>
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>
                    </StackPanel>
                </GroupBox>

                <!-- Выводы -->
                <GroupBox Header="🧠 Что это значит" Margin="0,0,0,15">
                    <ItemsControl x:Name="pnlConclusions" Margin="10" ItemsSource="{Binding ConclusionLines}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <TextBlock Text="{Binding Text}" Foreground="{Binding Foreground}" TextWrapping="Wrap" Margin="0,0,0,6"/>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </GroupBox>

                <!-- Отчёт -->
                <StackPanel Orientation="Horizontal" Margin="0,0,0,10">
                    <Button x:Name="btnCopyReport" Content="📋 Скопировать отчёт" Height="34" Width="200"
                            Margin="0,0,10,0" IsEnabled="{Binding IsCopyReportEnabled}"
                            ToolTip="Скопирует результаты теста и сведения о накопителе в буфер обмена."
                            Command="{Binding CopyReportCommand}"/>
                    <Button x:Name="btnSaveReport" Content="💾 Сохранить в файл" Height="34" Width="190"
                            IsEnabled="{Binding IsSaveReportEnabled}"
                            ToolTip="Откроет выбор файла и сохранит туда полный текстовый отчёт о тесте."
                            Command="{Binding SaveReportCommand}"/>
                </StackPanel>

            </StackPanel>
        </ScrollViewer>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Переписать `Ven4Tools/Views/Tabs/BenchmarkTab.xaml.cs`**

Полное содержимое файла:

```csharp
using System.Windows.Controls;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «Бенчмарк» — тонкая обёртка над <see cref="BenchmarkViewModel"/>.
    /// Вся логика перенесена в ViewModel при MVVM-миграции (2026-08-26, одиннадцатая
    /// и последняя вкладка серии — после неё клиент Ven4Tools полностью на MVVM).
    /// </summary>
    public partial class BenchmarkTab : UserControl
    {
        private readonly BenchmarkViewModel _viewModel = new();
        private bool _initialized;

        public BenchmarkTab()
        {
            InitializeComponent();
            DataContext = _viewModel;

            Loaded += async (_, _) =>
            {
                if (_initialized) return;
                _initialized = true;
                await _viewModel.InitializeAsync();
            };
        }
    }
}
```

- [ ] **Step 3: Удалить перенесённые partial-файлы code-behind**

```bash
git rm Ven4Tools/Views/Tabs/BenchmarkTab.Disks.cs Ven4Tools/Views/Tabs/BenchmarkTab.Run.cs Ven4Tools/Views/Tabs/BenchmarkTab.Report.cs
```

- [ ] **Step 4: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок, 0 предупреждений — во всех проектах, включая `Ven4Tools.ClientUITests`.

- [ ] **Step 5: Прогнать весь юнит-набор**

Run: `dotnet test tests/Ven4Tools.Tests -c Release`
Expected: без регрессий (было 500 после WindowsUpdateTab + новые из `BenchmarkViewModelTests`).

- [ ] **Step 6: Грep-проверка TwoWay-риска перед коммитом**

```bash
grep -n "progressBenchmark" Ven4Tools/Views/Tabs/BenchmarkTab.xaml
```

Убедиться, что у `progressBenchmark` есть `Mode=OneWay` на биндинге `Value=`.

- [ ] **Step 7: Commit**

```bash
git add Ven4Tools/Views/Tabs/BenchmarkTab.xaml Ven4Tools/Views/Tabs/BenchmarkTab.xaml.cs
git commit -m "refactor(benchmark): BenchmarkTab — тонкая обёртка над BenchmarkViewModel"
```

---

### Task 3: Верификация — регрессия существующих тестов

**Files:**
- Не создаёт и не меняет файлы.

**Interfaces:**
- Не применимо.

- [ ] **Step 1: Полная сборка Release**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0/0.

- [ ] **Step 2: Юнит-тесты целиком на VenchWork**

Run (на VenchWork): `dotnet test tests/Ven4Tools.Tests -c Release`
Expected: без регрессий относительно числа тестов после Task 2.

- [ ] **Step 3: Существующие UI-тесты на VenchWork**

Run (на VenchWork): `dotnet test Ven4Tools.ClientUITests -c Release --filter "FullyQualifiedName~BenchmarkTabTests|FullyQualifiedName~KeyButtonsSmokeTests"`
Expected: все 4 метода `BenchmarkTabTests` зелёные, включая `Бенчмарк_ПрогонНаБыстромПрофилеДаётРезультаты` (реальный прогон ~1 минута, создание/удаление временного файла 1 ГиБ). Это самый долгий и содержательный UI-тест во всей серии — не тревожиться, если прогон занимает 2-4 минуты, это ожидаемо для данного набора (сам полезный бенчмарк уже под минуту).

**Если UI-прогон не укладывается в 10-15 минут (с учётом ожидаемой ~2-4-минутной длительности самого бенчмарка)** — не ждать дальше: ребутнуть VenchWork / подключить Opus 5 для диагностики / искать причину самостоятельно, начиная с `%LOCALAPPDATA%\Ven4Tools\crash_last.json` (см. `feedback_ui_test_hang_escalation` в памяти).

- [ ] **Step 4: Финальный коммит верификации**

```bash
git add -A
git status
git commit -m "test(benchmark): MVVM-миграция BenchmarkTab проверена на VenchWork" --allow-empty
```

- [ ] **Step 5: Финальное цельное ревью ветки**

Обязательный шаг перед мерджем. Пакет для ревью: `scripts/review-package <merge-base main mvvm-benchmarktab> HEAD`. **Явно поручить ревьюеру**:
1. `progressBenchmark.Value` — `Mode=OneWay` на месте.
2. Двойной вызов `RefreshWarningsAsync` через каскад `SelectedDiskOption`→`FillVolumeOptions`→`SelectedVolumeOption` — сохранён (не «оптимизирован» до одного вызова), токен `_warningsToken` реально защищает.
3. Схема `AutomationId` (`txtP{N}Name`/`txtP{N}Read`/`txtP{N}ReadSub`/`txtP{N}Write`/`txtP{N}WriteSub`) — совпадает с оригиналом для всех 4 строк, не только для протестированной `txtP0Read`.
4. Внешний контракт `MainWindow.xaml.cs:32,221-223` — не сломан.

- [ ] **Step 6: Merge + push в `main`** (без дополнительного вопроса — автономная сессия)

```bash
git checkout main
git merge --ff-only mvvm-benchmarktab
dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental
git push origin main
git branch -d mvvm-benchmarktab
```

Перед пушем — обязательно проверить все коммиты ветки на `Claude-Session`-трейлер: `git log main..mvvm-benchmarktab --format="%B" | grep -i claude` (должно быть пусто).

---

## После задачи

Смержено и запушено в `main`. **Это последняя вкладка серии MVVM-миграции** — клиент Ven4Tools полностью переходит на MVVM (одиннадцать вкладок: Debloater/History/About/Activation/Network/Office/Installed/Diagnostics/System/WindowsUpdate/Benchmark, плюс ранее мигрированная CatalogTab). Обновить `agent_context.md`/`feature_map.md`, если там фиксировалось состояние миграции, и записать итоговую сводку серии в память.
