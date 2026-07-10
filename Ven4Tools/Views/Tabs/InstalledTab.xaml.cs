using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public class InstalledApp : INotifyPropertyChanged
    {
        public string Name      { get; set; } = "";
        public string WingetId  { get; set; } = "";
        public string Version   { get; set; } = "";

        private string _available = "";
        public string Available
        {
            get => _available;
            set { _available = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasUpdate)); }
        }

        public string Source    { get; set; } = "";

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        private bool _isProcessing;
        public bool IsProcessing
        {
            get => _isProcessing;
            set { _isProcessing = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanAct)); }
        }

        public bool HasUpdate        => !string.IsNullOrWhiteSpace(Available) && Available != "Unknown";
        public bool CanAct           => !IsProcessing;
        public bool IsVerified       => Source.Equals("winget", StringComparison.OrdinalIgnoreCase)
                                     || Source.Equals("msstore", StringComparison.OrdinalIgnoreCase);
        public bool IsUnknownSource  => string.IsNullOrWhiteSpace(Source) || Source.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

        public string SourceDisplay
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Source) || Source.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    return "❓ Неизвестный";
                if (Source.Equals("winget", StringComparison.OrdinalIgnoreCase))
                    return "✔ winget";
                if (Source.Equals("msstore", StringComparison.OrdinalIgnoreCase))
                    return "✔ Store";
                return Source;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class InstalledTab : UserControl
    {
        private List<InstalledApp> _allApps = new();

        // Фоновая предзагрузка — запускается из MainWindow.Loaded, до открытия вкладки.
        // Первое открытие вкладки просто awaits уже идущую задачу вместо нового winget list.
        private static Task? _preloadTask;
        private static volatile string? _cachedRawOutput;

        // Синхронизация доступа к _preloadTask и _cachedRawOutput: защита от гонки
        // при одновременных вызовах (предзагрузка из MainWindow vs открытие вкладки vs «Обновить»)
        private static readonly object _preloadLock = new object();

        public static void StartPreload()
        {
            lock (_preloadLock)
            {
                if (_preloadTask != null) return;
                _preloadTask = Task.Run(async () =>
                {
                    try
                    {
                        var (_, output) = await WingetRunner.RunAsync(
                            "list --accept-source-agreements --disable-interactivity");
                        _cachedRawOutput = output;
                    }
                    catch { _cachedRawOutput = string.Empty; }
                });
            }
        }

        public InstalledTab()
        {
            InitializeComponent();
            Loaded += (_, _) => _ = LoadAppsAsync();
        }

        public void ShowUpdatesFilter()
        {
            chkOnlyUpdates.IsChecked = true;
            ApplyFilter();
        }

        // ── Загрузка ────────────────────────────────────────────────────────────

        private async Task LoadAppsAsync()
        {
            ShowState("loading");

            string rawOutput;
            Task? preload;
            lock (_preloadLock) { preload = _preloadTask; }
            if (preload != null)
            {
                txtLoadingMsg.Text = preload.IsCompleted
                    ? "⏳ Загрузка списка приложений..."
                    : "⏳ Почти готово, дожидаемся предзагрузки...";
                try { await preload; } catch { }
                // Чтение и обнуление кэша — атомарно под блокировкой
                lock (_preloadLock)
                {
                    rawOutput = _cachedRawOutput ?? string.Empty;
                    _preloadTask = null;
                    _cachedRawOutput = null;
                }
            }
            else
            {
                txtLoadingMsg.Text = "⏳ Получение списка установленных приложений...";
                var (_, output) = await WingetRunner.RunAsync(
                    "list --accept-source-agreements --disable-interactivity");
                rawOutput = output;
            }

            try
            {
                _allApps = ParseWingetList(rawOutput);
                ApplyFilter();
                ShowState(_allApps.Count == 0 ? "empty" : "list");
                UpdateStats();
            }
            catch (Exception ex)
            {
                ShowState("loading");
                txtLoadingMsg.Text = $"❌ Ошибка: {ex.Message}";
            }
        }

        private static List<InstalledApp> ParseWingetList(string raw)
        {
            var result = new List<InstalledApp>();
            if (string.IsNullOrWhiteSpace(raw)) return result;

            // Убрать ANSI, нормализовать переводы строк
            var lines = WingetRunner.StripAnsi(raw).Replace("\r", "").Split('\n');

            // Ищем строку-заголовок: поддерживаем английский и русский вывод winget
            int headerIdx = Array.FindIndex(lines, l =>
                (l.Contains("Name") && l.Contains("Id") && l.Contains("Version")) ||
                (l.Contains("Имя")  && l.Contains("ИД") && l.Contains("Версия")));
            if (headerIdx < 0) return result;

            string header = lines[headerIdx];
            bool isRu = !header.Contains("Name");

            string nameCol      = isRu ? "Имя"      : "Name";
            string idCol        = isRu ? "ИД"        : "Id";
            string versionCol   = isRu ? "Версия"    : "Version";
            string availableCol = isRu ? "Доступна"  : "Available";
            string sourceCol    = isRu ? "Источник"  : "Source";

            // Убрать мусор до начала заголовка "Name"/"Имя" (ANSI-артефакты, отступы)
            int namePos = header.IndexOf(nameCol, StringComparison.Ordinal);
            if (namePos < 0) return result;
            int offset = namePos;

            // Позиции колонок относительно начала первой колонки
            int colName      = 0;
            int colId        = header.IndexOf(idCol,        namePos, StringComparison.Ordinal) - offset;
            int colVersion   = header.IndexOf(versionCol,   namePos, StringComparison.Ordinal) - offset;
            int colAvailable = header.IndexOf(availableCol, namePos, StringComparison.Ordinal) - offset;
            int colSource    = header.IndexOf(sourceCol,    namePos, StringComparison.Ordinal) - offset;
            if (colId <= 0 || colVersion <= 0) return result;
            if (colAvailable < 0) colAvailable = -1;
            if (colSource    < 0) colSource    = -1;

            bool started = false;
            for (int i = headerIdx + 1; i < lines.Length; i++)
            {
                string rawLine = lines[i];
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    if (started) break; // пустая строка = начало футера
                    continue;
                }

                // Пропускаем строку-разделитель из дефисов
                string t = rawLine.Trim();
                if (t.Length >= 5 && t.All(c => c == '-' || c == ' ')) continue;

                // Выровнять строку по offset заголовка
                string line = rawLine.Length > offset ? rawLine.Substring(offset) : rawLine;

                string name      = Extract(line, colName,    colId);
                string id        = Extract(line, colId,      colVersion);
                string version   = Extract(line, colVersion, colAvailable >= 0 ? colAvailable : line.Length);
                string available = colAvailable >= 0 ? Extract(line, colAvailable, colSource >= 0 ? colSource : line.Length) : "";
                string source    = colSource    >= 0 ? Extract(line, colSource,    line.Length) : "";

                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(id)) continue;

                started = true;
                result.Add(new InstalledApp
                {
                    Name      = name.Trim(),
                    WingetId  = id.Trim(),
                    Version   = version.Trim(),
                    Available = available.Trim(),
                    Source    = source.Trim()
                });
            }

            return result;
        }

        private static string Extract(string line, int from, int to)
        {
            if (from >= line.Length) return "";
            int end = Math.Min(to, line.Length);
            return line.Substring(from, end - from);
        }

        // ── Фильтрация ─────────────────────────────────────────────────────────

        private void ApplyFilter()
        {
            if (lstApps == null) return;
            string search = txtSearch.Text.Trim().ToLowerInvariant();

            IEnumerable<InstalledApp> filtered = _allApps;

            if (rdbUnknown.IsChecked == true)
                filtered = filtered.Where(a => a.IsUnknownSource);

            if (chkOnlyUpdates?.IsChecked == true)
                filtered = filtered.Where(a => a.HasUpdate);

            if (!string.IsNullOrEmpty(search))
                filtered = filtered.Where(a =>
                    a.Name.ToLowerInvariant().Contains(search) ||
                    a.WingetId.ToLowerInvariant().Contains(search));

            // Сортировка отображаемого списка
            filtered = (cmbSort?.SelectedIndex ?? 0) switch
            {
                1 => filtered.OrderBy(a => a.Version, StringComparer.OrdinalIgnoreCase),          // по версии
                2 => filtered.OrderByDescending(a => a.HasUpdate)                                 // сначала с обновлениями
                             .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
                _ => filtered.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)              // по имени
            };

            lstApps.ItemsSource = filtered.ToList();
            UpdateStats();
            UpdateSelectAllState();
        }

        private void UpdateStats()
        {
            int total   = _allApps.Count;
            int updates = _allApps.Count(a => a.HasUpdate);
            int unknown = _allApps.Count(a => a.IsUnknownSource);
            txtStats.Text = $"Всего: {total}  |  Обновлений: {updates}  |  Неизвестных: {unknown}";
        }

        // ── Событие-обработчики ────────────────────────────────────────────────

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void FilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

        private void CmbSort_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                btnRefresh.IsEnabled = false;
                // Сброс кэша предзагрузки — "Обновить" всегда идёт напрямую в winget
                lock (_preloadLock)
                {
                    _preloadTask = null;
                    _cachedRawOutput = null;
                }
                await LoadAppsAsync();
            }
            catch (Exception ex) { Log($"❌ Ошибка: {ex.Message}"); }
            finally { btnRefresh.IsEnabled = true; }
        }

        // ── Обновить всё (winget upgrade --all) ─────────────────────────────────

        private async void BtnUpgradeAll_Click(object sender, RoutedEventArgs e)
        {
            // Общий семафор с каталогом/историей/Windows Update — иначе winget
            // upgrade --all может пойти параллельно с установкой из другой вкладки
            // (конфликт msiexec, ошибка 1618).
            if (InstallationService.IsBusy)
            {
                MessageBox.Show(
                    "Дождитесь завершения текущей установки, затем повторите попытку.",
                    "Установка занята", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var res = MessageBox.Show(
                "Обновить все приложения через winget?\n\nЭто может занять продолжительное время.",
                "Обновить всё", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            btnUpgradeAll.IsEnabled = false;
            btnRefresh.IsEnabled = false;
            Log("⬆ Запуск обновления всех приложений (winget upgrade --all)...");
            await InstallationService.InstallSemaphore.WaitAsync();
            try
            {
                int code = await WingetRunner.RunStreamingAsync(
                    "upgrade --all --silent --include-unknown --accept-package-agreements --accept-source-agreements",
                    msg => Log(msg));
                if (code == 0)
                    Log("✅ Обновление всех приложений завершено");
                else if (code == 3010 || code == unchecked((int)0x8A15002C))
                    Log("✅ Обновление завершено. Для применения некоторых обновлений требуется перезагрузка.");
                else if (code == unchecked((int)0x8A15002B) || code == unchecked((int)0x8A150014))
                    Log("⚠ Некоторые обновления недоступны — версии в источнике не подходят для данной системы.");
                else
                    Log($"⚠ winget завершился с кодом {code}");
            }
            catch (Exception ex)
            {
                Log($"❌ Ошибка обновления: {ex.Message}");
            }
            finally
            {
                InstallationService.InstallSemaphore.Release();
                btnUpgradeAll.IsEnabled = true;
                btnRefresh.IsEnabled = true;
                // Обновляем список установленных приложений после завершения
                await LoadAppsAsync();
            }
        }

        private void ChkSelectAll_Click(object sender, RoutedEventArgs e)
        {
            bool check = chkSelectAll.IsChecked == true;
            var visible = lstApps.ItemsSource as IEnumerable<InstalledApp>;
            if (visible == null) return;
            foreach (var app in visible)
                if (app.CanAct && app.HasUpdate)
                    app.IsSelected = check;
            UpdateUpdateSelectedButton();
        }

        private void ItemCheckBox_Click(object sender, RoutedEventArgs e)
        {
            UpdateUpdateSelectedButton();
            UpdateSelectAllState();
        }

        private void UpdateSelectAllState()
        {
            var visible = (lstApps.ItemsSource as IEnumerable<InstalledApp>)?.Where(a => a.HasUpdate && a.CanAct).ToList();
            if (visible == null || visible.Count == 0)
            {
                chkSelectAll.IsChecked = false;
                return;
            }
            int selected = visible.Count(a => a.IsSelected);
            chkSelectAll.IsChecked = selected == visible.Count ? true :
                                     selected == 0 ? false : (bool?)null;
        }

        private void UpdateUpdateSelectedButton()
        {
            var visible = lstApps.ItemsSource as IEnumerable<InstalledApp>;
            var selected = visible?.Where(a => a.IsSelected).ToList();
            btnUpdateSelected.IsEnabled    = selected?.Any(a => a.HasUpdate) == true;
            btnUninstallSelected.IsEnabled = selected?.Count > 0;
        }

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (((Button)sender).Tag is not InstalledApp app) return;
                if (InstallationService.IsBusy)
                {
                    MessageBox.Show("Дождитесь завершения текущей установки, затем повторите попытку.",
                        "Установка занята", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                await UpdateAppAsync(app);
            }
            catch (Exception ex) { Log($"❌ Ошибка: {ex.Message}"); }
        }

        private async void BtnUninstall_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (((Button)sender).Tag is not InstalledApp app) return;
                if (InstallationService.IsBusy)
                {
                    MessageBox.Show("Дождитесь завершения текущей установки, затем повторите попытку.",
                        "Установка занята", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var res = MessageBox.Show(
                    $"Удалить «{app.Name}»?",
                    "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res != MessageBoxResult.Yes) return;

                await UninstallAppAsync(app);
            }
            catch (Exception ex) { Log($"❌ Ошибка: {ex.Message}"); }
        }

        private async void BtnUpdateSelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (InstallationService.IsBusy)
                {
                    MessageBox.Show("Дождитесь завершения текущей установки, затем повторите попытку.",
                        "Установка занята", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var visible = (lstApps.ItemsSource as IEnumerable<InstalledApp>)
                    ?.Where(a => a.IsSelected && a.HasUpdate).ToList();
                if (visible == null || visible.Count == 0) return;

                if (visible.Count >= 2)
                {
                    var rpAnswer = MessageBox.Show(
                        $"Будет обновлено {visible.Count} приложений.\n\nСоздать точку восстановления Windows перед обновлением?",
                        "Точка восстановления",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);

                    if (rpAnswer == MessageBoxResult.Cancel) return;

                    if (rpAnswer == MessageBoxResult.Yes)
                    {
                        Log("🛡️ Создаю точку восстановления...");
                        bool rpOk = await SystemRestoreService.CreateRestorePointAsync("Ven4Tools — перед массовым обновлением");
                        Log(rpOk ? "✅ Точка восстановления создана" : "⚠️ Точка восстановления не создана (можно продолжать)");
                    }
                }

                btnUpdateSelected.IsEnabled = false;
                foreach (var app in visible)
                    await UpdateAppAsync(app);
            }
            catch (Exception ex) { Log($"❌ Ошибка: {ex.Message}"); }
            finally { btnUpdateSelected.IsEnabled = true; }
        }

        // ── Операции winget ────────────────────────────────────────────────────

        private async Task UpdateAppAsync(InstalledApp app)
        {
            app.IsProcessing = true;
            Log($"⬆ Обновление {app.Name}...");
            // Общий семафор с каталогом/историей/Windows Update — исключает параллельный
            // msiexec (ошибка 1618) при обновлении одновременно с установкой из другой вкладки.
            await InstallationService.InstallSemaphore.WaitAsync();
            try
            {
                // Усечённый в списке ID (winget list рисует "…" при узкой колонке) не пройдёт
                // валидацию WingetRunner.ValidateArgs — не пытаемся, чтобы не ловить неясную ошибку.
                if (string.IsNullOrWhiteSpace(app.WingetId) || app.WingetId.Contains('…'))
                {
                    Log($"⚠ {app.Name}: ID приложения усечён winget — обновление недоступно");
                    return;
                }

                // RunStreamingAsync: живой прогресс в лог + 45-минутный таймаут
                // (RunAsync с 120с убивал winget на больших пакетах).
                // --locale en-US не используется — на части систем даёт пустой вывод (см. agent_context.md).
                string args = $"upgrade --id \"{app.WingetId}\" --silent --accept-package-agreements --accept-source-agreements";
                int code = await WingetRunner.RunStreamingAsync(args, line => Log($"  {line}"),
                    TimeSpan.FromMinutes(15));
                if (code == 0)
                {
                    app.Available = "";
                    Dispatcher.Invoke(() => { ApplyFilter(); UpdateStats(); });
                    Log($"✅ {app.Name} обновлён");
                }
                else if (code == 3010 || code == unchecked((int)0x8A15002C))
                {
                    // 3010 = Windows installer reboot required; 0x8A15002C = winget reboot required to finish
                    app.Available = "";
                    Dispatcher.Invoke(() => { ApplyFilter(); UpdateStats(); });
                    Log($"✅ {app.Name} обновлён (требуется перезагрузка для завершения)");
                }
                else if (code == unchecked((int)0x8A15002B) || code == unchecked((int)0x8A150014))
                {
                    // 0x8A15002B / 0x8A150014 = No applicable upgrade found:
                    // доступная версия в источнике не подходит для данной системы (архитектура, требования и т.п.)
                    Log($"⚠ {app.Name}: обновление недоступно — версия {app.Available} не применима к данной системе");
                }
                else if (code == unchecked((int)0x80072EE2) || code == unchecked((int)0x80072EFE))
                {
                    // 0x80072EE2 = WININET_E_TIMEOUT / 0x80072EFE = WININET_E_CONNECTION_ABORTED —
                    // источник (msstore и др.) недоступен по сети, обновление не применено
                    Log($"⚠ {app.Name}: ошибка сети — источник недоступен, попробуйте позже");
                }
                else if (code != -1)
                {
                    Log($"⚠ {app.Name}: winget завершился с кодом {code}");
                }
            }
            catch (Exception ex) { Log($"❌ {app.Name}: {ex.Message}"); }
            finally
            {
                InstallationService.InstallSemaphore.Release();
                app.IsProcessing = false;
            }
        }

        private async Task UninstallAppAsync(InstalledApp app)
        {
            app.IsProcessing = true;
            Log($"🗑 Удаление {app.Name}...");
            // Общий семафор — см. комментарий в UpdateAppAsync.
            await InstallationService.InstallSemaphore.WaitAsync();
            try
            {
                bool ok = await TryUninstallAsync(app);
                if (ok)
                {
                    _allApps.Remove(app);
                    ApplyFilter();
                    Log($"✅ {app.Name} удалён");
                }
                else
                {
                    Log($"⚠ {app.Name}: деинсталлятор не найден");
                }
            }
            catch (Exception ex) { Log($"❌ {app.Name}: {ex.Message}"); }
            finally
            {
                InstallationService.InstallSemaphore.Release();
                app.IsProcessing = false;
            }
        }

        private static async Task<bool> TryUninstallAsync(InstalledApp app)
        {
            // Попытка 1: winget uninstall по ID (работает для пакетов с непустым Source)
            if (!string.IsNullOrWhiteSpace(app.WingetId) && !app.WingetId.Contains('…'))
            {
                string args = $"uninstall --id \"{app.WingetId}\" --silent --accept-source-agreements";
                var (exitCode, _) = await WingetRunner.RunAsync(args);
                // Проверяем код выхода, а не локализованный текст вывода winget.
                // 0 = успех, 0x8A150014 = пакет не установлен (нечего удалять — считаем успехом).
                if (exitCode == 0 || exitCode == unchecked((int)0x8A150014))
                    return true;
            }

            // Попытка 2: найти строку UninstallString в реестре по DisplayName.
            // Скан реестра — в Task.Run, чтобы не блокировать UI-поток.
            string? uninstallString = await Task.Run(() => FindUninstallString(app.Name));
            if (uninstallString != null)
                return await RunUninstallStringAsync(uninstallString);

            return false;
        }

        private static string? FindUninstallString(string displayName)
        {
            string[] keys = {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            var hives = new[] { Registry.LocalMachine };

            foreach (var hive in hives)
            foreach (var keyPath in keys)
            {
                using var root = hive.OpenSubKey(keyPath);
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var entry = root.OpenSubKey(sub);
                    if (entry == null) continue;
                    var name = entry.GetValue("DisplayName")?.ToString();
                    if (name != null && name.Equals(displayName, StringComparison.OrdinalIgnoreCase))
                        return entry.GetValue("UninstallString")?.ToString();
                }
            }
            return null;
        }

        private static async Task<bool> RunUninstallStringAsync(string uninstallString)
        {
            // Тихий режим: msiexec /x ... /quiet, NSIS /S, Inno /SILENT
            string cmd = uninstallString.Trim();
            Process? p;
            if (cmd.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase) ||
                cmd.StartsWith("msiexec", StringComparison.OrdinalIgnoreCase))
            {
                var productCode = Regex.Match(cmd, @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}");
                if (!productCode.Success)
                    return false;

                var startInfo = new ProcessStartInfo("msiexec.exe")
                {
                    UseShellExecute = true,
                    Verb            = "runas",
                    CreateNoWindow  = true
                };
                startInfo.ArgumentList.Add("/x");
                startInfo.ArgumentList.Add(productCode.Value);
                startInfo.ArgumentList.Add("/quiet");
                startInfo.ArgumentList.Add("/norestart");
                p = Process.Start(startInfo);
            }
            else
            {
                // NSIS / Inno / другие — пробуем добавить /S или /SILENT
                string exe = cmd, args = "";
                if (cmd.StartsWith("\""))
                {
                    int end = cmd.IndexOf('"', 1);
                    if (end > 0) { exe = cmd.Substring(1, end - 1); args = cmd.Substring(end + 1).Trim(); }
                }
                else
                {
                    int sp = cmd.IndexOf(' ');
                    if (sp > 0) { exe = cmd.Substring(0, sp); args = cmd.Substring(sp + 1).Trim(); }
                }
                if (!args.Contains("/S") && !args.Contains("/SILENT") && !args.Contains("/silent"))
                    args = "/S " + args;
                p = Process.Start(new ProcessStartInfo(exe, args)
                    { UseShellExecute = true, Verb = "runas" });
            }
            if (p == null) return false;
            // Асинхронное ожидание с таймаутом 120 секунд — UI-поток не блокируется
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(); } catch { }
                return false;
            }
            // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED — удаление прошло успешно
            return p.ExitCode == 0 || p.ExitCode == 3010;
        }

        // ── Вспомогательные ───────────────────────────────────────────────────

        private void ShowState(string state)
        {
            Dispatcher.Invoke(() =>
            {
                pnlLoading.Visibility  = state == "loading" ? Visibility.Visible   : Visibility.Collapsed;
                pnlEmpty.Visibility    = state == "empty"   ? Visibility.Visible   : Visibility.Collapsed;
                listScroll.Visibility  = state == "list"    ? Visibility.Visible   : Visibility.Collapsed;
            });
        }

        private static void Log(string msg) => AppLogger.Write(msg);

        // ── Групповое удаление ────────────────────────────────────────────────

        private async void BtnUninstallSelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (InstallationService.IsBusy)
                {
                    MessageBox.Show("Дождитесь завершения текущей установки, затем повторите попытку.",
                        "Установка занята", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var selected = (lstApps.ItemsSource as IEnumerable<InstalledApp>)
                    ?.Where(a => a.IsSelected && a.CanAct).ToList();
                if (selected == null || selected.Count == 0) return;

                string list = string.Join("\n", selected.Take(10).Select(a => $"  • {a.Name}"));
                if (selected.Count > 10) list += $"\n  ... и ещё {selected.Count - 10}";

                var res = MessageBox.Show(
                    $"Удалить {selected.Count} приложений?\n\n{list}",
                    "Подтверждение удаления", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;

                if (selected.Count >= 2)
                {
                    var rpAnswer = MessageBox.Show(
                        $"Будет удалено {selected.Count} приложений.\n\nСоздать точку восстановления Windows перед удалением?",
                        "Точка восстановления",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);
                    if (rpAnswer == MessageBoxResult.Cancel) return;
                    if (rpAnswer == MessageBoxResult.Yes)
                    {
                        Log("🛡️ Создаю точку восстановления...");
                        bool rpOk = await SystemRestoreService.CreateRestorePointAsync("Ven4Tools — перед групповым удалением");
                        Log(rpOk ? "✅ Точка восстановления создана" : "⚠️ Точка восстановления не создана (можно продолжать)");
                    }
                }

                btnUninstallSelected.IsEnabled = false;

                foreach (var app in selected)
                    await UninstallAppAsync(app);

                btnUninstallSelected.IsEnabled = selected.Any(a => a.CanAct);
            }
            catch (Exception ex) { Log($"❌ Ошибка: {ex.Message}"); btnUninstallSelected.IsEnabled = true; }
        }

        // ── Экспорт / Импорт ─────────────────────────────────────────────────

        private async void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title    = "Экспорт списка приложений",
                Filter   = "Winget package list (*.winget)|*.winget|JSON (*.json)|*.json",
                FileName = $"Ven4Tools-export-{DateTime.Now:yyyy-MM-dd}"
            };
            if (dlg.ShowDialog() != true) return;

            btnExport.IsEnabled = false;
            Log($"📤 Экспорт в {System.IO.Path.GetFileName(dlg.FileName)}...");
            try
            {
                var (_, output) = await WingetRunner.RunAsync($"export -o \"{dlg.FileName}\" --accept-source-agreements");
                bool ok = System.IO.File.Exists(dlg.FileName);
                Log(ok ? $"✅ Экспортировано → {dlg.FileName}"
                       : $"⚠ winget: {output.Trim().Split('\n').LastOrDefault()}");
            }
            catch (Exception ex) { Log($"❌ Ошибка экспорта: {ex.Message}"); }
            finally { btnExport.IsEnabled = true; }
        }

        private async void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Импорт списка приложений",
                Filter = "Winget package list (*.winget)|*.winget|JSON (*.json)|*.json"
            };
            if (dlg.ShowDialog() != true) return;

            btnImport.IsEnabled = false;
            Log($"📥 Импорт из {System.IO.Path.GetFileName(dlg.FileName)}...");
            Log("⏳ Это может занять несколько минут...");
            try
            {
                var (_, output) = await WingetRunner.RunAsync($"import -i \"{dlg.FileName}\" --accept-package-agreements --accept-source-agreements");
                bool ok = output.Contains("успешно") || output.Contains("successfully") || output.Contains("All packages");
                Log(ok ? "✅ Импорт завершён"
                       : $"⚠ {output.Trim().Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l))}");
                if (ok) await LoadAppsAsync();
            }
            catch (Exception ex) { Log($"❌ Ошибка импорта: {ex.Message}"); }
            finally { btnImport.IsEnabled = true; }
        }

        // ── Вспомогательные ───────────────────────────────────────────────────
    }
}
