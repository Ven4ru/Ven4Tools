# DiagnosticsTab MVVM Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Перенести логику вкладки «Диагностика» (`DiagnosticsTab`, 741 строка в 6 partial-файлах code-behind) из code-behind в `DiagnosticsViewModel`, оставив `DiagnosticsTab.xaml`/`.xaml.cs` тонкой обёрткой. Восьмая вкладка серии MVVM-миграции.

**Architecture:** `DiagnosticsViewModel : INotifyPropertyChanged`, partial-класс по образцу `OfficeViewModel.*`/`InstalledViewModel.*` — `DiagnosticsViewModel.cs` (ядро + вспомогательные типы `DiagnosticsTextRow`/`RebootCardInfo`), `.SystemInfo.cs`, `.TurboBoost.cs`, `.RebootHistory.cs`, `.Checks.cs`, `.Report.cs`. Программное создание DOM-элементов (`pnlDisks.Children.Add(...)`, `Expander`+`TextBox`-карточки) заменяется биндингом `ItemsControl` на коллекции.

**Tech Stack:** .NET 8, WPF, xUnit.

## Global Constraints

- Поведение 1:1 с оригиналом, кроме адаптаций:
  1. `SystemHealthService`/`AppLogger`/`TrustedExecutablePaths`/`Registry`/`Clipboard`/`MessageBox`/`Process.Start` — из VM напрямую (устоявшийся паттерн).
  2. `event Action? GoToWindowsUpdate` остаётся на самом `DiagnosticsTab` (внешний контракт, `MainWindow.xaml.cs:213`); VM получает свой `GoToWindowsUpdate`, code-behind ретранслирует.
  3. `_initialized` (защита повторного `Loaded`) остаётся в code-behind — WPF-lifecycle забота, не VM-концерн.
  4. **`CopyFullReport()` обязан подставлять текст плейсхолдера `"Нажмите «Запустить диагностику»"` для разделов «Диски»/«Ошибки Windows Update», если диагностика ещё не запускалась** (`ShowPlaceholders == true`) — оригинал собирает отчёт из ЖИВЫХ дочерних `TextBlock` контролов (`pnlDisks.Children.OfType<TextBlock>()`), которые до первого запуска содержат именно этот текст; в VM `DiskRows`/`WuRows` пустые до первого запуска (плейсхолдер — отдельный элемент XAML, не часть коллекции), поэтому наивный `string.Join(..., DiskRows.Select(r => r.Text))` даёТ пустую строку вместо текста плейсхолдера — расхождение с оригиналом. Компенсировать явной проверкой `ShowPlaceholders` внутри `CopyFullReport()` (код ниже это уже делает — не пропустить при транскрипции).
- **Гейт реентерабельности** (урок NetworkTab): `RunDiagnosticsAsync`/`RunClearWuCacheAsync` начинаются с `if (СвойБизиФлаг) return;` первой строкой. Остальные 9 команд (Turbo Boost, логи, копирование, экспорт, фикс быстрого запуска) в оригинале НЕ имеют защиты от повторного клика — не добавлять её самовольно (это было бы расширением объёма, не переносом).
- `internal static Brush ResolveBrush(string resourceKey)` — тестируемый хелпер (см. паттерн `OfficeViewModel`/`NetworkViewModel`): `(Application.Current?.TryFindResource(resourceKey) as Brush) ?? Brushes.White`.
- **Обязательная правка теста, не относящаяся к XAML/VM напрямую**: `tests/Ven4Tools.Tests/ButtonToolTipCoverageTests.cs` содержит `[InlineData("Ven4Tools/Views/Tabs/DiagnosticsTab.RebootHistory.cs", "fixBtn")]` — тест, специально написанный под то, что кнопка «Отключить быстрый запуск» раньше создавалась программно в C#. После миграции она становится обычной XAML-кнопкой с `ToolTip` и покрывается общим тестом `AllFunctionalXamlButtonsHaveExplanations` — эту строку `InlineData` нужно УДАЛИТЬ (Task 2), иначе тест падает (файла/переменной с таким содержимым там больше нет).
- Все `x:Name`, участвующие в UI-тестах, сохраняются дословно: `btnDiagnosticsTab` (в MainWindow, не эта вкладка), `btnRunDiagnostics`, `btnCopyFullReport`, `btnCopySystemInfo`, `btnOpenWindowsUpdate`.
- Никакой статический `IsEnabled` на кнопках не нужен — `CanExecute` + `CommandManager`.
- Коммиты — на русском, без Claude/AI-атрибуции.
- Ветка `mvvm-diagnosticstab` уже создана от `main`, спека закоммичена (`0004a0f`).

---

### Task 1: `DiagnosticsViewModel` (6 файлов) + юнит-тесты

**Files:**
- Create: `Ven4Tools/ViewModels/DiagnosticsViewModel.cs`
- Create: `Ven4Tools/ViewModels/DiagnosticsViewModel.SystemInfo.cs`
- Create: `Ven4Tools/ViewModels/DiagnosticsViewModel.TurboBoost.cs`
- Create: `Ven4Tools/ViewModels/DiagnosticsViewModel.RebootHistory.cs`
- Create: `Ven4Tools/ViewModels/DiagnosticsViewModel.Checks.cs`
- Create: `Ven4Tools/ViewModels/DiagnosticsViewModel.Report.cs`
- Test: `tests/Ven4Tools.Tests/DiagnosticsViewModelTests.cs`

**Interfaces:**
- Consumes: `Ven4Tools.Services.SystemHealthService` (`GetRebootHistoryAsync`/`GetDiskHealthAsync`/`GetWindowsUpdateFailuresAsync`/`GetHardwareEventsAsync`/`IsFastStartupEnabled`/`DisableFastStartupAsync`/`ClearWindowsUpdateCacheAsync`), `Ven4Tools.Services.RebootDiagnosis`/`RebootCategory`/`DiskHealth`/`DiskHealthInfo`/`WindowsUpdateFailure`/`HardwareEventsSummary` (все `internal`, тот же assembly), `Ven4Tools.Services.AppLogger`, `Ven4Tools.Services.TrustedExecutablePaths`, `Ven4Tools.Helpers.SizeFormatter.BytesToGBWhole`, `Ven4Tools.ViewModels.RelayCommand`/`RelayCommand.FromAsync`.
- Produces: `Ven4Tools.ViewModels.DiagnosticsTextRow`/`RebootCardInfo`, `Ven4Tools.ViewModels.DiagnosticsViewModel` — публичные свойства `OSVersionText`/`ProcessorText`/`RAMText`/`AppVersionText`/`LatestLogText`/`HealthBadgeText`/`HealthBadgeBrush`/`LastRunText`/`ShowPlaceholders`/`DiskRows`/`RebootStatusRow`/`ShowRebootStatusRow`/`RebootCards`/`ShowDisableFastStartupButton`/`WuRows`/`WuButtonsVisible`/`HardwareSummaryText`/`HardwareRawText`/`HardwareRawVisible`/`TurboBoostStatusText`/`IsRunningDiagnostics`/`IsClearingWuCache`; команды `RunDiagnosticsCommand`/`CopySystemInfoCommand`/`OpenLogsCommand`/`OpenLatestLogCommand`/`ClearLogsCommand`/`DisableTurboBoostCommand`/`EnableTurboBoostCommand`/`ClearWuCacheCommand`/`OpenWindowsUpdateCommand`/`CopyFullReportCommand`/`DisableFastStartupCommand`; событие `GoToWindowsUpdate`; публичный `Task InitializeAsync()`; `internal static Brush ResolveBrush(string)`.

- [ ] **Step 1: Создать `Ven4Tools/ViewModels/DiagnosticsViewModel.cs`**

Полное содержимое файла:

```csharp
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

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
    /// Activation/Network/Office/Installed) без изменения поведения — см.
    /// docs/superpowers/specs/2026-08-26-diagnosticstab-mvvm-design.md.
    /// Разбит на partial-файлы по образцу OfficeViewModel.*/InstalledViewModel.*.
    /// </summary>
    public sealed partial class DiagnosticsViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action? GoToWindowsUpdate;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        internal static Brush ResolveBrush(string resourceKey) =>
            (Application.Current?.TryFindResource(resourceKey) as Brush) ?? Brushes.White;

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

        private Brush _healthBadgeBrush = ResolveBrush("TextSecondary");
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
            private set { SetField(ref _isRunningDiagnostics, value); RunDiagnosticsCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isClearingWuCache;
        public bool IsClearingWuCache
        {
            get => _isClearingWuCache;
            private set { SetField(ref _isClearingWuCache, value); ClearWuCacheCommand.RaiseCanExecuteChanged(); }
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
            DisableTurboBoostCommand  = RelayCommand.FromAsync(_ => RunDisableTurboBoostAsync());
            EnableTurboBoostCommand   = RelayCommand.FromAsync(_ => RunEnableTurboBoostAsync());
            ClearWuCacheCommand       = RelayCommand.FromAsync(_ => RunClearWuCacheAsync(),    _ => !IsClearingWuCache);
            OpenWindowsUpdateCommand  = new RelayCommand(_ => GoToWindowsUpdate?.Invoke());
            CopyFullReportCommand     = new RelayCommand(_ => CopyFullReport());
            DisableFastStartupCommand = RelayCommand.FromAsync(_ => RunDisableFastStartupAsync());
        }

        public async Task InitializeAsync()
        {
            await LoadSystemInfoAsync();
            await RefreshTurboBoostStatusAsync();
        }
    }
}
```

- [ ] **Step 2: Создать `Ven4Tools/ViewModels/DiagnosticsViewModel.SystemInfo.cs`**

Полное содержимое файла:

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class DiagnosticsViewModel
    {
        private async Task LoadSystemInfoAsync()
        {
            try
            {
                string osVersion  = Environment.OSVersion.VersionString;
                var version       = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                string appVersion = version?.ToString() ?? "—";

                string processor = "Неизвестно";
                string ram       = "";

                await Task.Run(() =>
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                    {
                        foreach (var obj in searcher.Get())
                        {
                            processor = obj["Name"]?.ToString()?.Trim() ?? "Неизвестно";
                            break;
                        }
                    }

                    using (var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem"))
                    {
                        foreach (var obj in searcher.Get())
                        {
                            // TotalVisibleMemorySize от WMI приходит в КБ, не в байтах —
                            // домножаем перед общим байтовым форматтером.
                            long totalMemoryKB = Convert.ToInt64(obj["TotalVisibleMemorySize"]);
                            ram = Helpers.SizeFormatter.BytesToGBWhole(totalMemoryKB * 1024L);
                            break;
                        }
                    }
                });

                OSVersionText  = osVersion;
                ProcessorText  = processor;
                RAMText        = ram;
                AppVersionText = appVersion;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка загрузки информации о системе: {ex.Message}");
            }
        }

        private void CopySystemInfo()
        {
            try
            {
                string info = $"ОС: {OSVersionText}\n" +
                              $"Процессор: {ProcessorText}\n" +
                              $"ОЗУ: {RAMText}\n" +
                              $"Ven4Tools: {AppVersionText}";

                Clipboard.SetText(info);
                AppLogger.Write("📋 Информация о системе скопирована в буфер обмена");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка копирования: {ex.Message}");
            }
        }

        private void OpenLogs()
        {
            try
            {
                string logsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "logs");
                Directory.CreateDirectory(logsPath);
                // Путь в кавычках: он лежит в профиле пользователя, а имя учётной
                // записи Windows вполне может содержать пробел («Иван Петров») —
                // без кавычек explorer получил бы обрезанный по пробелу путь.
                Process.Start(TrustedExecutablePaths.ExplorerExe, $"\"{logsPath}\"");
                AppLogger.Write($"📁 Открыта папка логов: {logsPath}");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка открытия папки логов: {ex.Message}");
            }
        }

        private void OpenLatestLog()
        {
            try
            {
                string logsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "logs");
                if (!Directory.Exists(logsPath)) { AppLogger.Write("📋 Логов нет"); return; }

                var latestLog = Directory.GetFiles(logsPath, "install_*.log")
                    .OrderByDescending(f => f)
                    .FirstOrDefault();

                if (latestLog == null) { AppLogger.Write("📋 Файлы логов не найдены"); return; }

                var lines = File.ReadAllLines(latestLog);
                var preview = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 50)));
                LatestLogText = preview;

                // Кавычки обязательны по той же причине, что и у кнопки «Открыть папку
                // логов»: путь идёт через профиль пользователя, имя которого может
                // содержать пробел, и «блокнот» открыл бы не тот файл.
                Process.Start(new ProcessStartInfo { FileName = TrustedExecutablePaths.NotepadExe, Arguments = $"\"{latestLog}\"", UseShellExecute = true });
                AppLogger.Write($"📄 Открыт лог: {Path.GetFileName(latestLog)}");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка: {ex.Message}");
            }
        }

        private void ClearLogs()
        {
            var result = MessageBox.Show("Удалить все файлы логов?", "Подтверждение",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    string logsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "logs");
                    if (Directory.Exists(logsPath))
                    {
                        foreach (var file in Directory.GetFiles(logsPath))
                        {
                            File.Delete(file);
                        }
                    }
                    // Общий журнал приложения (app.log / app.old.log) лежит НЕ в подпапке
                    // logs, а уровнем выше — раньше он переживал очистку, хотя вопрос
                    // пользователю звучит «Удалить все файлы логов?». Именно там копятся
                    // сообщения вкладок и сервисов, поэтому обещание должно выполняться.
                    AppLogger.ClearAppLogFiles();
                    AppLogger.Write("🗑️ Логи очищены");
                }
                catch (Exception ex)
                {
                    AppLogger.Write($"❌ Ошибка очистки логов: {ex.Message}");
                }
            }
        }
    }
}
```

- [ ] **Step 3: Создать `Ven4Tools/ViewModels/DiagnosticsViewModel.TurboBoost.cs`**

Полное содержимое файла:

```csharp
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class DiagnosticsViewModel
    {
        // CurrentControlSet — псевдоним активного набора, а не жёсткий ControlSet001:
        // на системах, где активен ControlSet002 (после отказа предыдущей загрузки),
        // жёсткий путь писал бы в неактивный набор и пункт не появлялся бы в Панели управления.
        private const string TurboBoostRegPath = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\be337238-0d82-4146-a960-4f3749d470c7";

        private const string TurboSubgroup = "54533251-82be-4824-96c1-47b60b740d00";

        private const string TurboSetting  = "be337238-0d82-4146-a960-4f3749d470c7";

        // L8: обновляет текстовый статус текущего состояния Turbo Boost в UI.
        // Вызывается при загрузке вкладки и после включения/отключения.
        private async Task RefreshTurboBoostStatusAsync()
        {
            bool? state = await GetTurboBoostStateAsync();
            TurboBoostStatusText = state switch
            {
                true  => "Текущее состояние: ⚡ включён",
                false => "Текущее состояние: ❌ отключён",
                _     => "Текущее состояние: неизвестно"
            };
        }

        private async Task RunDisableTurboBoostAsync()
        {
            try
            {
                await ApplyTurboBoostAsync(false);
                await RefreshTurboBoostStatusAsync();
                AppLogger.Write("⚡ Турбобуст отключён");
                MessageBox.Show("✅ Турбобуст отключён.\nИзменение применено немедленно — перезагрузка не требуется.",
                    "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка при отключении турбобуста: {ex.Message}");
                MessageBox.Show("Не удалось отключить турбобуст. Запустите приложение от имени администратора и попробуйте ещё раз.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task RunEnableTurboBoostAsync()
        {
            try
            {
                await ApplyTurboBoostAsync(true);
                await RefreshTurboBoostStatusAsync();
                AppLogger.Write("⚡ Турбобуст включён");
                MessageBox.Show("✅ Турбобуст включён.\nИзменение применено немедленно — перезагрузка не требуется.",
                    "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка при включении турбобуста: {ex.Message}");
                MessageBox.Show("Не удалось включить турбобуст. Запустите приложение от имени администратора и попробуйте ещё раз.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ApplyTurboBoostAsync(bool enable)
        {
            int value = enable ? 1 : 0;

            // Применяем для AC (от сети) и DC (от батареи)
            await RunPowerCfgAsync($"-setacvalueindex SCHEME_CURRENT {TurboSubgroup} {TurboSetting} {value}");
            await RunPowerCfgAsync($"-setdcvalueindex SCHEME_CURRENT {TurboSubgroup} {TurboSetting} {value}");

            // Активируем схему чтобы применить изменения
            await RunPowerCfgAsync("-setactive SCHEME_CURRENT");

            // Делаем настройку видимой в панели управления
            SetTurboBoostAttributes(2);
        }

        private async Task<bool?> GetTurboBoostStateAsync()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = TrustedExecutablePaths.PowerCfgExe,
                    Arguments = $"/query SCHEME_CURRENT {TurboSubgroup} {TurboSetting}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };
                using var process = Process.Start(psi);
                if (process == null) return null;
                // Асинхронное чтение — не блокируем UI-поток
                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                // Языконезависимый разбор: powercfg локализует подписи строк
                // («Current AC Power Setting Index» на русской Windows выводится по-русски),
                // но значения «0x...» встречаются только в двух финальных строках —
                // текущий индекс AC (от сети) и DC (от батареи). Берём первый — AC.
                var matches = System.Text.RegularExpressions.Regex.Matches(output, @"0x([0-9A-Fa-f]+)");
                if (matches.Count > 0)
                    return Convert.ToInt32(matches[0].Groups[1].Value, 16) != 0;
            }
            catch (Exception ex)
            {
                // Иначе в UI просто появляется «неизвестно», а причина нигде не остаётся —
                // соседние обработчики турбобуста пишут свои ошибки в журнал так же.
                AppLogger.Write(ex, "❌ Не удалось определить состояние турбобуста");
            }
            return null;
        }

        private async Task RunPowerCfgAsync(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = TrustedExecutablePaths.PowerCfgExe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi) ?? throw new Exception("Не удалось запустить powercfg");
            // Читаем stdout и stderr асинхронно — иначе WaitForExit зависнет, если буфер
            // любого из них переполнится. WaitForExitAsync не блокирует UI-поток.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await stdoutTask;
            string err = await stderrTask;
            if (process.ExitCode != 0)
                throw new Exception($"powercfg завершился с ошибкой {process.ExitCode}: {err}");
        }

        private void SetTurboBoostAttributes(int value)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(TurboBoostRegPath, writable: true)
                    ?? Registry.LocalMachine.CreateSubKey(TurboBoostRegPath);
                key.SetValue("Attributes", value, RegistryValueKind.DWord);
            }
            catch { /* только видимость пункта в Панели управления — на сам турбобуст не влияет */ }
        }
    }
}
```

- [ ] **Step 4: Создать `Ven4Tools/ViewModels/DiagnosticsViewModel.RebootHistory.cs`**

Полное содержимое файла:

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class DiagnosticsViewModel
    {
        // Итоговый статус-бейдж собирается из результатов всех проверок —
        // эти два флага накапливаются при каждом запуске "Запустить диагностику"
        // (см. также disks/WU-часть в DiagnosticsViewModel.Checks.cs).
        private bool _lastRunHadCritical;
        private bool _lastRunHadWarning;

        private async Task<List<RebootDiagnosis>> RunRebootHistoryCheckAsync()
        {
            RebootStatusRow = null;
            ShowRebootStatusRow = false;
            RebootCards = Array.Empty<RebootCardInfo>();
            ShowDisableFastStartupButton = false;

            List<RebootDiagnosis> diagnoses;
            try
            {
                diagnoses = await SystemHealthService.GetRebootHistoryAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.RunRebootHistoryCheckAsync");
                RebootStatusRow = new DiagnosticsTextRow { Text = "Недоступно: не удалось прочитать журнал событий.", Foreground = ResolveBrush("StatusWarning") };
                ShowRebootStatusRow = true;
                return new List<RebootDiagnosis>();
            }

            if (diagnoses.Count == 0)
            {
                RebootStatusRow = new DiagnosticsTextRow { Text = "За последние 7 дней нештатных завершений работы не найдено.", Foreground = ResolveBrush("StatusSuccess") };
                ShowRebootStatusRow = true;
                return diagnoses;
            }

            bool anyFastStartupFailure = false;
            var cards = new List<RebootCardInfo>();
            foreach (var d in diagnoses)
            {
                if (d.Category == RebootCategory.Bsod) _lastRunHadCritical = true;
                else _lastRunHadWarning = true;
                if (d.Category == RebootCategory.FastStartupFailure) anyFastStartupFailure = true;

                cards.Add(BuildRebootCard(d));
            }

            RebootCards = cards;

            // Кнопку фикса показываем, только если быстрый запуск сейчас
            // действительно включён (или статус не удалось определить) —
            // иначе предлагали бы отключить то, что уже выключено (пользователь
            // мог сам исправить это между запусками диагностики).
            ShowDisableFastStartupButton = anyFastStartupFailure && SystemHealthService.IsFastStartupEnabled() != false;

            return diagnoses;
        }

        private static RebootCardInfo BuildRebootCard(RebootDiagnosis d)
        {
            string icon = d.Category switch
            {
                RebootCategory.Bsod => "🔴",
                RebootCategory.FastStartupFailure => "🟡",
                RebootCategory.PossiblePowerLoss => "🟡",
                _ => "⚪"
            };

            return new RebootCardInfo
            {
                Header = $"{icon} {d.TimeCreated:g} — {d.Summary}",
                RawDetails = d.RawDetails
            };
        }

        private async Task RunDisableFastStartupAsync()
        {
            var confirm = MessageBox.Show(
                "Отключить «Быстрый запуск»? Это уберёт файл гибернации и механизм резюме — «Завершение работы» станет полным холодным выключением.",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                await SystemHealthService.DisableFastStartupAsync();
                AppLogger.Write("🔧 Быстрый запуск отключён");
                MessageBox.Show("✅ Быстрый запуск отключён.", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка при отключении быстрого запуска: {ex.Message}");
                MessageBox.Show("Не удалось отключить быстрый запуск. Запустите приложение от имени администратора и попробуйте ещё раз.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
```

- [ ] **Step 5: Создать `Ven4Tools/ViewModels/DiagnosticsViewModel.Checks.cs`**

Полное содержимое файла:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class DiagnosticsViewModel
    {
        private async Task RunDiskCheckAsync()
        {
            DiskRows = Array.Empty<DiagnosticsTextRow>();
            try
            {
                var disks = await SystemHealthService.GetDiskHealthAsync();
                if (disks.Count == 0)
                {
                    DiskRows = new[] { new DiagnosticsTextRow { Text = "Диски не найдены.", Foreground = ResolveBrush("TextSecondary") } };
                    return;
                }
                var rows = new List<DiagnosticsTextRow>();
                foreach (var disk in disks)
                {
                    if (disk.Health is DiskHealth.Warning or DiskHealth.Unhealthy) _lastRunHadCritical = true;
                    string icon = disk.Health switch
                    {
                        DiskHealth.Healthy => "🟢",
                        DiskHealth.Warning => "🟡",
                        DiskHealth.Unhealthy => "🔴",
                        _ => "⚪"
                    };
                    string label = disk.Health switch
                    {
                        DiskHealth.Healthy => "исправен",
                        DiskHealth.Warning => "предупреждение",
                        DiskHealth.Unhealthy => "неисправен",
                        _ => "неизвестно"
                    };
                    rows.Add(new DiagnosticsTextRow
                    {
                        Text = $"{icon} {disk.Name} — {label}",
                        Foreground = ResolveBrush("TextPrimary")
                    });
                }
                DiskRows = rows;
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.RunDiskCheckAsync");
                DiskRows = new[] { new DiagnosticsTextRow { Text = "Недоступно: не удалось получить состояние дисков.", Foreground = ResolveBrush("StatusWarning") } };
            }
        }

        private async Task RunWindowsUpdateCheckAsync()
        {
            WuRows = Array.Empty<DiagnosticsTextRow>();
            WuButtonsVisible = false;
            try
            {
                var failures = await SystemHealthService.GetWindowsUpdateFailuresAsync();
                if (failures.Count == 0)
                {
                    WuRows = new[] { new DiagnosticsTextRow { Text = "За последние 7 дней ошибок обновления Windows не найдено.", Foreground = ResolveBrush("StatusSuccess") } };
                    return;
                }

                _lastRunHadWarning = true;
                WuRows = failures.Take(20)
                    .Select(f => new DiagnosticsTextRow { Text = $"🟡 {f.TimeCreated:g} — {f.Message}", Foreground = ResolveBrush("TextPrimary") })
                    .ToList();
                // Ошибки есть — предлагаем сразу перейти туда, где патчи можно
                // переустановить, не заставляя искать вкладку в меню вручную.
                WuButtonsVisible = true;
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.RunWindowsUpdateCheckAsync");
                WuRows = new[] { new DiagnosticsTextRow { Text = "Недоступно: не удалось прочитать журнал Windows Update.", Foreground = ResolveBrush("StatusWarning") } };
            }
        }

        private async Task RunClearWuCacheAsync()
        {
            if (IsClearingWuCache) return;

            var confirm = MessageBox.Show(
                "Остановить службы обновления Windows и очистить кэш загрузки? Службы будут перезапущены автоматически.",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            IsClearingWuCache = true;
            try
            {
                await SystemHealthService.ClearWindowsUpdateCacheAsync();
                AppLogger.Write("🧹 Кэш Windows Update очищен");
                MessageBox.Show("✅ Кэш Windows Update очищен, службы перезапущены.", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка очистки кэша Windows Update: {ex.Message}");
                MessageBox.Show("Не удалось очистить кэш. Запустите приложение от имени администратора и попробуйте ещё раз.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsClearingWuCache = false;
            }
        }

        private async Task RunHardwareEventsCheckAsync()
        {
            try
            {
                var summary = await SystemHealthService.GetHardwareEventsAsync();
                HardwareSummaryText =
                    $"Аппаратных ошибок (WHEA): {summary.WheaCount}. Сбоев видеодрайвера: {summary.DisplayDriverCrashCount}.";

                if (summary.RawEntries.Count > 0)
                {
                    HardwareRawText = string.Join(Environment.NewLine, summary.RawEntries);
                    HardwareRawVisible = true;
                }
                else
                {
                    HardwareRawVisible = false;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.RunHardwareEventsCheckAsync");
                HardwareSummaryText = "Недоступно: не удалось прочитать аппаратные события.";
            }
        }
    }
}
```

- [ ] **Step 6: Создать `Ven4Tools/ViewModels/DiagnosticsViewModel.Report.cs`**

Полное содержимое файла:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class DiagnosticsViewModel
    {
        private List<RebootDiagnosis> _lastRebootDiagnoses = new();

        private async Task RunDiagnosticsAsync()
        {
            if (IsRunningDiagnostics) return;

            IsRunningDiagnostics = true;
            HealthBadgeText = "Диагностика выполняется...";
            HealthBadgeBrush = ResolveBrush("TextSecondary");
            _lastRunHadCritical = false;
            _lastRunHadWarning = false;
            ShowPlaceholders = false;

            try
            {
                _lastRebootDiagnoses = await RunRebootHistoryCheckAsync();
                await RunDiskCheckAsync();
                await RunWindowsUpdateCheckAsync();
                await RunHardwareEventsCheckAsync();

                if (_lastRunHadCritical)
                {
                    HealthBadgeText = "🔴 Критично — есть находки, требующие внимания";
                    HealthBadgeBrush = ResolveBrush("StatusDanger");
                }
                else if (_lastRunHadWarning)
                {
                    HealthBadgeText = "🟡 Есть на что посмотреть";
                    HealthBadgeBrush = ResolveBrush("StatusWarning");
                }
                else
                {
                    HealthBadgeText = "🟢 Всё в порядке";
                    HealthBadgeBrush = ResolveBrush("StatusSuccess");
                }
                LastRunText = $"Последний запуск: {DateTime.Now:g}";
                AppLogger.Write("🔍 Диагностика ПК выполнена");
            }
            finally
            {
                IsRunningDiagnostics = false;
            }
        }

        private void CopyFullReport()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("=== Отчёт диагностики Ven4Tools ===");
                sb.AppendLine($"Время: {DateTime.Now:g}");
                sb.AppendLine();
                sb.AppendLine($"ОС: {OSVersionText}");
                sb.AppendLine($"Процессор: {ProcessorText}");
                sb.AppendLine($"ОЗУ: {RAMText}");
                sb.AppendLine($"Ven4Tools: {AppVersionText}");
                sb.AppendLine();
                sb.AppendLine("--- История перезагрузок и сбоев ---");
                if (_lastRebootDiagnoses.Count == 0)
                {
                    sb.AppendLine("Нештатных завершений работы за последние 7 дней не найдено (или диагностика ещё не запускалась).");
                }
                else
                {
                    foreach (var d in _lastRebootDiagnoses)
                        sb.AppendLine($"[{d.Category}] {d.TimeCreated:g} — {d.Summary} | {d.RawDetails}");
                }
                sb.AppendLine();
                sb.AppendLine("--- Диски ---");
                // ShowPlaceholders — диагностика ещё не запускалась: оригинал собирал этот
                // раздел из живых дочерних TextBlock, которые до первого запуска содержат
                // текст плейсхолдера «Нажмите «Запустить диагностику»» — воспроизводим то же.
                sb.AppendLine(ShowPlaceholders
                    ? "Нажмите «Запустить диагностику»"
                    : string.Join(Environment.NewLine, DiskRows.Select(r => r.Text)));
                sb.AppendLine();
                sb.AppendLine("--- Ошибки Windows Update ---");
                sb.AppendLine(ShowPlaceholders
                    ? "Нажмите «Запустить диагностику»"
                    : string.Join(Environment.NewLine, WuRows.Select(r => r.Text)));
                sb.AppendLine();
                sb.AppendLine("--- Аппаратные и драйверные события ---");
                sb.AppendLine(HardwareSummaryText);
                if (HardwareRawVisible)
                    sb.AppendLine(HardwareRawText);

                Clipboard.SetText(sb.ToString());
                AppLogger.Write("📤 Полный отчёт диагностики скопирован в буфер обмена");
                MessageBox.Show("✅ Отчёт скопирован в буфер обмена.", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка копирования отчёта: {ex.Message}");
            }
        }
    }
}
```

- [ ] **Step 7: Написать `tests/Ven4Tools.Tests/DiagnosticsViewModelTests.cs`**

Полное содержимое файла:

```csharp
using Ven4Tools.ViewModels;
using Xunit;

namespace Ven4Tools.Tests
{
    public class DiagnosticsViewModelTests
    {
        [Fact]
        public void Конструктор_УстанавливаетДефолтныеЗначения()
        {
            var vm = new DiagnosticsViewModel();

            Assert.Equal("Загрузка...", vm.OSVersionText);
            Assert.Equal("Загрузка...", vm.ProcessorText);
            Assert.Equal("Загрузка...", vm.RAMText);
            Assert.Equal("", vm.AppVersionText);
            Assert.Equal("Нажмите «Последний лог» для просмотра...", vm.LatestLogText);
            Assert.Equal("Диагностика ещё не запускалась", vm.HealthBadgeText);
            Assert.Equal("", vm.LastRunText);
            Assert.True(vm.ShowPlaceholders);
            Assert.Empty(vm.DiskRows);
            Assert.Empty(vm.WuRows);
            Assert.Empty(vm.RebootCards);
            Assert.Null(vm.RebootStatusRow);
            Assert.False(vm.ShowRebootStatusRow);
            Assert.False(vm.ShowDisableFastStartupButton);
            Assert.False(vm.WuButtonsVisible);
            Assert.Equal("Нажмите «Запустить диагностику»", vm.HardwareSummaryText);
            Assert.Equal("", vm.HardwareRawText);
            Assert.False(vm.HardwareRawVisible);
            Assert.Equal("Текущее состояние: определяется...", vm.TurboBoostStatusText);
            Assert.False(vm.IsRunningDiagnostics);
            Assert.False(vm.IsClearingWuCache);
        }

        [Fact]
        public void КомандыБезCanExecute_ИзначальноTrue()
        {
            var vm = new DiagnosticsViewModel();

            Assert.True(vm.CopySystemInfoCommand.CanExecute(null));
            Assert.True(vm.OpenLogsCommand.CanExecute(null));
            Assert.True(vm.OpenLatestLogCommand.CanExecute(null));
            Assert.True(vm.ClearLogsCommand.CanExecute(null));
            Assert.True(vm.DisableTurboBoostCommand.CanExecute(null));
            Assert.True(vm.EnableTurboBoostCommand.CanExecute(null));
            Assert.True(vm.OpenWindowsUpdateCommand.CanExecute(null));
            Assert.True(vm.CopyFullReportCommand.CanExecute(null));
            Assert.True(vm.DisableFastStartupCommand.CanExecute(null));
        }

        [Fact]
        public void БизиКоманды_CanExecute_ИзначальноTrue()
        {
            var vm = new DiagnosticsViewModel();

            Assert.True(vm.RunDiagnosticsCommand.CanExecute(null));
            Assert.True(vm.ClearWuCacheCommand.CanExecute(null));
        }

        [Fact]
        public void OpenWindowsUpdateCommand_ПоднимаетСобытие()
        {
            var vm = new DiagnosticsViewModel();
            bool raised = false;
            vm.GoToWindowsUpdate += () => raised = true;

            vm.OpenWindowsUpdateCommand.Execute(null);

            Assert.True(raised);
        }

        [Fact]
        public void ResolveBrush_БезApplication_ПадаетВБелыйФолбэк()
        {
            Assert.Null(System.Windows.Application.Current);

            var brush = DiagnosticsViewModel.ResolveBrush("TextSecondary");

            Assert.Same(System.Windows.Media.Brushes.White, brush);
        }

        [Fact]
        public void HealthBadgeBrush_ДефолтноеЗначение_БелыйФолбэк()
        {
            var vm = new DiagnosticsViewModel();

            Assert.Same(System.Windows.Media.Brushes.White, vm.HealthBadgeBrush);
        }
    }
}
```

- [ ] **Step 8: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок, 0 предупреждений.

- [ ] **Step 9: Commit**

```bash
git add Ven4Tools/ViewModels/DiagnosticsViewModel.cs Ven4Tools/ViewModels/DiagnosticsViewModel.SystemInfo.cs Ven4Tools/ViewModels/DiagnosticsViewModel.TurboBoost.cs Ven4Tools/ViewModels/DiagnosticsViewModel.RebootHistory.cs Ven4Tools/ViewModels/DiagnosticsViewModel.Checks.cs Ven4Tools/ViewModels/DiagnosticsViewModel.Report.cs tests/Ven4Tools.Tests/DiagnosticsViewModelTests.cs
git commit -m "feat(diagnostics): DiagnosticsViewModel (6 файлов) + юнит-тесты"
```

---

### Task 2: Переписать `DiagnosticsTab.xaml`/`DiagnosticsTab.xaml.cs` на тонкую обёртку

**Files:**
- Modify: `Ven4Tools/Views/Tabs/DiagnosticsTab.xaml`
- Modify: `Ven4Tools/Views/Tabs/DiagnosticsTab.xaml.cs`
- Delete: `Ven4Tools/Views/Tabs/DiagnosticsTab.SystemInfo.cs`
- Delete: `Ven4Tools/Views/Tabs/DiagnosticsTab.TurboBoost.cs`
- Delete: `Ven4Tools/Views/Tabs/DiagnosticsTab.RebootHistory.cs`
- Delete: `Ven4Tools/Views/Tabs/DiagnosticsTab.Checks.cs`
- Delete: `Ven4Tools/Views/Tabs/DiagnosticsTab.Report.cs`
- Modify: `tests/Ven4Tools.Tests/ButtonToolTipCoverageTests.cs`

**Interfaces:**
- Consumes: `Ven4Tools.ViewModels.DiagnosticsViewModel` (Task 1) — все публичные члены.
- Produces: `DiagnosticsTab` с публичным членом сверх конструктора — `event Action? GoToWindowsUpdate` (внешний контракт, `MainWindow.xaml.cs:213`).

- [ ] **Step 1: Переписать `Ven4Tools/Views/Tabs/DiagnosticsTab.xaml`**

Полное содержимое файла:

```xml
<UserControl x:Class="Ven4Tools.Views.Tabs.DiagnosticsTab"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="{DynamicResource ContentBackground}">
    <UserControl.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis"/>
    </UserControl.Resources>
    <Grid Margin="20">
        <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
        <StackPanel Margin="0,0,0,16">
            <TextBlock Text="Диагностика" Style="{StaticResource PageTitleStyle}"/>
            <TextBlock Text="Состояние ПК: перезагрузки, диски, обновления Windows, драйверы"
                       Foreground="{DynamicResource TextSecondary}" Margin="0,4,0,0"/>
        </StackPanel>

        <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto">
            <StackPanel Margin="8,0,8,8">

                <!-- Шапка: запуск диагностики + статус-бейдж -->
                <Border Background="{DynamicResource CardBackground}" CornerRadius="6" Padding="14" Margin="0,0,0,15">
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="Auto"/>
                            <ColumnDefinition Width="*"/>
                        </Grid.ColumnDefinitions>
                        <Button x:Name="btnRunDiagnostics" Content="🔍 Запустить диагностику"
                                ToolTip="Проверит состояние системы, дисков, недавние сбои, Windows Update и аппаратные события. Ничего не исправляет автоматически."
                                Height="36" Width="220" HorizontalAlignment="Left"
                                Command="{Binding RunDiagnosticsCommand}"/>
                        <StackPanel Grid.Column="1" HorizontalAlignment="Right" Orientation="Horizontal" VerticalAlignment="Center">
                            <Ellipse x:Name="dotHealthBadge" Width="10" Height="10" Fill="{Binding HealthBadgeBrush}" Margin="0,0,8,0"/>
                            <TextBlock x:Name="txtHealthBadge" Text="{Binding HealthBadgeText}" Foreground="{DynamicResource TextPrimary}" VerticalAlignment="Center"/>
                        </StackPanel>
                        <TextBlock x:Name="txtLastRun" Text="{Binding LastRunText}" Grid.Column="1" HorizontalAlignment="Right" VerticalAlignment="Bottom"
                                   Margin="0,20,0,0" FontSize="10" Foreground="{DynamicResource TextSecondary}"/>
                    </Grid>
                </Border>

                <!-- Информация о системе -->
                <GroupBox Header="💻 Информация о системе" Margin="0,0,0,15">
                    <StackPanel Margin="10">
                        <Grid Margin="0,5">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="120"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                            <TextBlock Text="ОС:" Foreground="{DynamicResource TextSecondary}"/>
                            <TextBlock x:Name="txtOSVersion" Grid.Column="1" Text="{Binding OSVersionText}" Foreground="{DynamicResource TextPrimary}"/>
                        </Grid>
                        <Grid Margin="0,5">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="120"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                            <TextBlock Text="Процессор:" Foreground="{DynamicResource TextSecondary}"/>
                            <TextBlock x:Name="txtProcessor" Grid.Column="1" Text="{Binding ProcessorText}" Foreground="{DynamicResource TextPrimary}"/>
                        </Grid>
                        <Grid Margin="0,5">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="120"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                            <TextBlock Text="ОЗУ:" Foreground="{DynamicResource TextSecondary}"/>
                            <TextBlock x:Name="txtRAM" Grid.Column="1" Text="{Binding RAMText}" Foreground="{DynamicResource TextPrimary}"/>
                        </Grid>
                        <Grid Margin="0,5">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="120"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                            <TextBlock Text="Версия Ven4Tools:" Foreground="{DynamicResource TextSecondary}"/>
                            <TextBlock x:Name="txtAppVersion" Grid.Column="1" Text="{Binding AppVersionText}" Foreground="{DynamicResource TextPrimary}"/>
                        </Grid>
                        <Button x:Name="btnCopySystemInfo" Content="📋 Копировать информацию"
                                ToolTip="Скопирует показанные сведения о Windows, процессоре, памяти и версии Ven4Tools."
                                Height="30" Width="180" Margin="0,15,0,0" HorizontalAlignment="Left"
                                Command="{Binding CopySystemInfoCommand}"/>
                    </StackPanel>
                </GroupBox>

                <!-- История перезагрузок и сбоев -->
                <GroupBox Header="🔁 История перезагрузок и сбоев (7 дней)" Margin="0,0,0,15">
                    <StackPanel x:Name="pnlRebootHistory" Margin="10">
                        <TextBlock x:Name="txtRebootHistoryPlaceholder" Text="Нажмите «Запустить диагностику»"
                                   Foreground="{DynamicResource TextSecondary}"
                                   Visibility="{Binding ShowPlaceholders, Converter={StaticResource BoolToVis}}"/>
                        <TextBlock Text="{Binding RebootStatusRow.Text}" Foreground="{Binding RebootStatusRow.Foreground}"
                                   Visibility="{Binding ShowRebootStatusRow, Converter={StaticResource BoolToVis}}"/>
                        <ItemsControl ItemsSource="{Binding RebootCards}">
                            <ItemsControl.ItemTemplate>
                                <DataTemplate>
                                    <Expander Header="{Binding Header}" Margin="0,0,0,6">
                                        <TextBox Text="{Binding RawDetails}"
                                                 IsReadOnly="True"
                                                 TextWrapping="Wrap"
                                                 FontFamily="Consolas"
                                                 FontSize="10"
                                                 Background="{DynamicResource CardBackground}"
                                                 Foreground="{DynamicResource TextPrimary}"
                                                 Margin="10,4,10,8"/>
                                    </Expander>
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>
                        <Button x:Name="btnDisableFastStartup" Content="🔧 Отключить быстрый запуск"
                                ToolTip="После подтверждения отключит быстрый запуск Windows и удалит файл гибернации."
                                Height="32" Width="240" HorizontalAlignment="Left" Margin="0,10,0,0"
                                Visibility="{Binding ShowDisableFastStartupButton, Converter={StaticResource BoolToVis}}"
                                Command="{Binding DisableFastStartupCommand}"/>
                    </StackPanel>
                </GroupBox>

                <!-- Диски -->
                <GroupBox Header="💾 Диски" Margin="0,0,0,15">
                    <StackPanel x:Name="pnlDisks" Margin="10">
                        <TextBlock x:Name="txtDisksPlaceholder" Text="Нажмите «Запустить диагностику»"
                                   Foreground="{DynamicResource TextSecondary}"
                                   Visibility="{Binding ShowPlaceholders, Converter={StaticResource BoolToVis}}"/>
                        <ItemsControl ItemsSource="{Binding DiskRows}">
                            <ItemsControl.ItemTemplate>
                                <DataTemplate>
                                    <TextBlock Text="{Binding Text}" Foreground="{Binding Foreground}" Margin="0,2,0,2"/>
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>
                    </StackPanel>
                </GroupBox>

                <!-- Обновления Windows -->
                <GroupBox Header="🪟 Ошибки Windows Update (7 дней)" Margin="0,0,0,15">
                    <StackPanel Margin="10">
                        <StackPanel x:Name="pnlWindowsUpdateFailures">
                            <TextBlock x:Name="txtWuPlaceholder" Text="Нажмите «Запустить диагностику»"
                                       Foreground="{DynamicResource TextSecondary}"
                                       Visibility="{Binding ShowPlaceholders, Converter={StaticResource BoolToVis}}"/>
                            <ItemsControl ItemsSource="{Binding WuRows}">
                                <ItemsControl.ItemTemplate>
                                    <DataTemplate>
                                        <TextBlock Text="{Binding Text}" Foreground="{Binding Foreground}" TextWrapping="Wrap" Margin="0,2,0,2"/>
                                    </DataTemplate>
                                </ItemsControl.ItemTemplate>
                            </ItemsControl>
                        </StackPanel>
                        <!-- Обе кнопки появляются только когда ошибки обновления реально
                             найдены: чистка кэша лечит частую причину, а переход на вкладку
                             «Обновления Windows» позволяет тут же повторить установку. -->
                        <StackPanel Orientation="Horizontal" Margin="0,10,0,0">
                            <Button x:Name="btnClearWuCache" Content="🧹 Очистить кэш Windows Update"
                                    ToolTip="После подтверждения остановит службы обновления, очистит их кэш и снова запустит службы."
                                    Height="30" Width="240" HorizontalAlignment="Left"
                                    Visibility="{Binding WuButtonsVisible, Converter={StaticResource BoolToVis}}"
                                    Command="{Binding ClearWuCacheCommand}"/>
                            <Button x:Name="btnOpenWindowsUpdate" Content="🪟 Открыть Windows Update →"
                                    ToolTip="Откроет вкладку «Обновления Windows», где можно заново проверить и установить не установившиеся патчи."
                                    Height="30" Width="240" Margin="10,0,0,0" HorizontalAlignment="Left"
                                    Visibility="{Binding WuButtonsVisible, Converter={StaticResource BoolToVis}}"
                                    Command="{Binding OpenWindowsUpdateCommand}"/>
                        </StackPanel>
                    </StackPanel>
                </GroupBox>

                <!-- Аппаратные и драйверные события -->
                <GroupBox Header="⚠️ Аппаратные и драйверные события (7 дней)" Margin="0,0,0,15">
                    <StackPanel Margin="10">
                        <TextBlock x:Name="txtHardwareSummary" Text="{Binding HardwareSummaryText}"
                                   Foreground="{DynamicResource TextSecondary}"/>
                        <TextBox x:Name="txtHardwareRaw" Text="{Binding HardwareRawText}" Margin="0,10,0,0" Height="80"
                                 Background="{DynamicResource CardBackground}" Foreground="{DynamicResource TextPrimary}"
                                 FontFamily="Consolas" FontSize="10" IsReadOnly="True"
                                 VerticalScrollBarVisibility="Auto" TextWrapping="Wrap"
                                 Visibility="{Binding HardwareRawVisible, Converter={StaticResource BoolToVis}}"/>
                    </StackPanel>
                </GroupBox>

                <!-- Turbo Boost -->
                <GroupBox Header="⚡ Turbo Boost (Intel)" Margin="0,0,0,15">
                    <StackPanel>
                        <TextBlock Text="Управление технологией Intel Turbo Boost"
                                   Foreground="{DynamicResource TextSecondary}" Margin="10,5,10,0"/>
                        <TextBlock x:Name="txtTurboBoostStatus" Text="{Binding TurboBoostStatusText}"
                                   Foreground="{DynamicResource TextPrimary}" FontWeight="SemiBold"
                                   Margin="10,8,10,0"/>
                        <StackPanel Orientation="Horizontal" Margin="10,10">
                            <Button x:Name="btnDisableTurboBoost" Content="❌ Отключить Turbo Boost"
                                    ToolTip="Изменит параметры электропитания и немедленно отключит Turbo Boost процессора."
                                    Height="35" Width="180" Margin="0,0,10,0"
                                    Command="{Binding DisableTurboBoostCommand}"/>
                            <Button x:Name="btnEnableTurboBoost" Content="✅ Включить Turbo Boost"
                                    ToolTip="Изменит параметры электропитания и немедленно включит Turbo Boost процессора. Перезагрузка не требуется."
                                    Height="35" Width="180"
                                    Command="{Binding EnableTurboBoostCommand}"/>
                        </StackPanel>
                        <TextBlock Text="ℹ️ Изменение применяется немедленно, перезагрузка не требуется"
                                   Foreground="{DynamicResource TextSecondary}" FontSize="11" Margin="10,5,10,10"/>
                    </StackPanel>
                </GroupBox>

                <!-- Логи -->
                <GroupBox Header="📋 Логи приложения" Margin="0,0,0,15">
                    <StackPanel>
                        <StackPanel Orientation="Horizontal" Margin="10,10,10,5">
                            <Button x:Name="btnOpenLogs" Content="📁 Открыть папку"
                                    ToolTip="Откроет в Проводнике папку, где Ven4Tools хранит диагностические журналы."
                                    Height="35" Width="150" Margin="0,0,10,0"
                                    Command="{Binding OpenLogsCommand}"/>
                            <Button x:Name="btnOpenLatestLog" Content="📄 Последний лог"
                                    ToolTip="Загрузит содержимое самого свежего журнала в поле просмотра ниже."
                                    Height="35" Width="155" Margin="0,0,10,0"
                                    Command="{Binding OpenLatestLogCommand}"/>
                            <Button x:Name="btnClearLogs" Content="🗑️ Очистить"
                                    ToolTip="После подтверждения удалит сохранённые файлы журналов Ven4Tools."
                                    Height="35" Width="100"
                                    Command="{Binding ClearLogsCommand}"/>
                        </StackPanel>
                        <TextBox x:Name="txtLatestLog" Text="{Binding LatestLogText}" Margin="10,0,10,10" Height="100"
                                 Background="{DynamicResource CardBackground}" Foreground="{DynamicResource TextPrimary}"
                                 FontFamily="Consolas" FontSize="10" IsReadOnly="True"
                                 VerticalScrollBarVisibility="Auto" TextWrapping="Wrap"/>
                    </StackPanel>
                </GroupBox>

                <!-- Экспорт отчёта -->
                <Button x:Name="btnCopyFullReport" Content="📤 Скопировать полный отчёт"
                        ToolTip="Соберёт результаты диагностики в текстовый отчёт и скопирует его в буфер обмена."
                        Height="34" Width="240" HorizontalAlignment="Left" Margin="0,0,0,10"
                        Command="{Binding CopyFullReportCommand}"/>

            </StackPanel>
        </ScrollViewer>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Переписать `Ven4Tools/Views/Tabs/DiagnosticsTab.xaml.cs`**

Полное содержимое файла:

```csharp
using System;
using System.Windows.Controls;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «Диагностика» — тонкая обёртка над <see cref="DiagnosticsViewModel"/>.
    /// Вся логика перенесена в ViewModel при MVVM-миграции (2026-08-26, восьмая
    /// вкладка после DebloaterTab/HistoryTab/AboutTab/ActivationTab/NetworkTab/
    /// OfficeTab/InstalledTab). Единственный публичный член сверх конструктора —
    /// event GoToWindowsUpdate (внешний контракт, MainWindow.xaml.cs).
    /// </summary>
    public partial class DiagnosticsTab : UserControl
    {
        private readonly DiagnosticsViewModel _viewModel = new();
        private bool _initialized = false;

        public event Action? GoToWindowsUpdate;

        public DiagnosticsTab()
        {
            InitializeComponent();
            DataContext = _viewModel;
            _viewModel.GoToWindowsUpdate += () => GoToWindowsUpdate?.Invoke();

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
git rm Ven4Tools/Views/Tabs/DiagnosticsTab.SystemInfo.cs Ven4Tools/Views/Tabs/DiagnosticsTab.TurboBoost.cs Ven4Tools/Views/Tabs/DiagnosticsTab.RebootHistory.cs Ven4Tools/Views/Tabs/DiagnosticsTab.Checks.cs Ven4Tools/Views/Tabs/DiagnosticsTab.Report.cs
```

- [ ] **Step 4: Убрать устаревшую запись из `ButtonToolTipCoverageTests.cs`**

В `tests/Ven4Tools.Tests/ButtonToolTipCoverageTests.cs` удалить строку:

```csharp
    [InlineData("Ven4Tools/Views/Tabs/DiagnosticsTab.RebootHistory.cs", "fixBtn")]
```

из атрибутов `[Theory]` метода `DynamicButtonsHaveExplanations` (оставить только две записи про `PinsStripController.cs`). Кнопка «Отключить быстрый запуск» больше не создаётся программно в C# — она обычная XAML-кнопка `btnDisableFastStartup` с `ToolTip`, автоматически покрывается тестом `AllFunctionalXamlButtonsHaveExplanations`.

- [ ] **Step 5: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок, 0 предупреждений — во всех проектах, включая `Ven4Tools.ClientUITests`.

- [ ] **Step 6: Commit**

```bash
git add Ven4Tools/Views/Tabs/DiagnosticsTab.xaml Ven4Tools/Views/Tabs/DiagnosticsTab.xaml.cs tests/Ven4Tools.Tests/ButtonToolTipCoverageTests.cs
git commit -m "refactor(diagnostics): DiagnosticsTab — тонкая обёртка над DiagnosticsViewModel"
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
Expected: было 466/466 после InstalledTab (см. память `project_ven4tools_mvvm_migration_installedtab_2026_08_26`) + 6 новых из `DiagnosticsViewModelTests` = 472/472. **Также обязательно убедиться, что `ButtonToolTipCoverageTests` все зелёные** (правка Task 2 Step 4 не сломала общий тест).

- [ ] **Step 3: Существующие UI-тесты на VenchWork**

Run (на VenchWork): `dotnet test Ven4Tools.ClientUITests -c Release --filter "FullyQualifiedName~DiagnosticsTabTests|FullyQualifiedName~KeyButtonsSmokeTests|FullyQualifiedName~Top5FeaturesUiTests"`
Expected: `Диагностика_ОткрываетсяИЗапускается`, `Диагностика_КопированиеОтчёта` и релевантные тесты остальных классов — зелёные, не хуже прежнего.

**Если UI-прогон не укладывается в 10-15 минут** — не ждать дальше: ребутнуть VenchWork / подключить Opus 5 для диагностики / искать причину самостоятельно, начиная с `%LOCALAPPDATA%\Ven4Tools\crash_last.json` (см. `feedback_ui_test_hang_escalation` в памяти).

- [ ] **Step 4: Финальный коммит верификации**

```bash
git add -A
git status
git commit -m "test(diagnostics): MVVM-миграция DiagnosticsTab проверена на VenchWork" --allow-empty
```

- [ ] **Step 5: Финальное цельное ревью ветки**

Обязательный шаг перед мерджем — точечные ревью Task 1/Task 2 структурно не видят межзадачные пробелы; в предыдущих 7 вкладках подряд этот шаг находил реальные находки (в шестой — крашащий баг, в седьмой — баг порядка TwoWay-биндинга радиокнопок). Пакет для ревью: `scripts/review-package <merge-base main mvvm-diagnosticstab> HEAD`. **Явно поручить ревьюеру перепроверить п.4 Global Constraints (плейсхолдер в CopyFullReport)** и убедиться, что `ButtonToolTipCoverageTests` учтён.

- [ ] **Step 6: Merge + push в `main`** (без дополнительного вопроса — автономная сессия)

```bash
git checkout main
git merge --ff-only mvvm-diagnosticstab
dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental
git push origin main
git branch -d mvvm-diagnosticstab
```

Перед пушем — обязательно проверить все коммиты ветки на `Claude-Session`-трейлер: `git log main..mvvm-diagnosticstab --format="%B" | grep -i claude` (должно быть пусто).

---

## После задачи

Смержено и запушено в `main`. Следующая по сложности вкладка — `SystemTab` (1014 строк, 8 файлов, самая рискованная из оставшихся — содержит известный баг `ThemeService`) — тот же процесс, новая ветка от `main`.
