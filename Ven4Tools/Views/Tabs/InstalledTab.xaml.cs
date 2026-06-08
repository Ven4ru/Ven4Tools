using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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

        public event Action<string>? LogMessage;

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
            txtLoadingMsg.Text = "⏳ Получение списка установленных приложений...";

            try
            {
                _allApps = await RunWingetListAsync();
                ApplyFilter();
                ShowState(_allApps.Count == 0 ? "empty" : "list");
                UpdateStats();
            }
            catch (Exception ex)
            {
                txtLoadingMsg.Text = $"❌ Ошибка: {ex.Message}";
            }
        }

        private static async Task<List<InstalledApp>> RunWingetListAsync()
        {
            var output = await RunWingetAsync("list --accept-source-agreements");
            return ParseWingetList(output);
        }

        // Убрать ANSI escape-коды и управляющие символы из вывода winget
        private static readonly System.Text.RegularExpressions.Regex _ansiRegex =
            new(@"\x1B(?:\[[0-9;]*[mGKHFABCDsuJh]|\][^\x07]*\x07|[()][0-9A-Za-z])",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string StripAnsi(string s)
            => _ansiRegex.Replace(s, "");

        private static List<InstalledApp> ParseWingetList(string raw)
        {
            var result = new List<InstalledApp>();
            if (string.IsNullOrWhiteSpace(raw)) return result;

            // Убрать ANSI, нормализовать переводы строк
            var lines = StripAnsi(raw).Replace("\r", "").Split('\n');

            // Ищем строку-заголовок по ключевым словам (работает на любом языке системы,
            // т.к. winget всегда выводит заголовок таблицы на английском)
            int headerIdx = Array.FindIndex(lines,
                l => l.Contains("Name") && l.Contains("Id") && l.Contains("Version"));
            if (headerIdx < 0) return result;

            string header = lines[headerIdx];

            // Убрать мусор до начала "Name" (ANSI-артефакты, отступы)
            int namePos = header.IndexOf("Name", StringComparison.Ordinal);
            if (namePos < 0) return result;
            int offset = namePos;

            // Позиции колонок относительно начала "Name"
            int colName      = 0;
            int colId        = header.IndexOf("Id",        namePos, StringComparison.Ordinal) - offset;
            int colVersion   = header.IndexOf("Version",   namePos, StringComparison.Ordinal) - offset;
            int colAvailable = header.IndexOf("Available", namePos, StringComparison.Ordinal) - offset;
            int colSource    = header.IndexOf("Source",    namePos, StringComparison.Ordinal) - offset;
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
            btnRefresh.IsEnabled = false;
            await LoadAppsAsync();
            btnRefresh.IsEnabled = true;
        }

        // ── Обновить всё (winget upgrade --all) ─────────────────────────────────

        private async void BtnUpgradeAll_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show(
                "Обновить все приложения через winget?\n\nЭто может занять продолжительное время.",
                "Обновить всё", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            btnUpgradeAll.IsEnabled = false;
            btnRefresh.IsEnabled = false;
            Log("⬆ Запуск обновления всех приложений (winget upgrade --all)...");
            try
            {
                int code = await RunWingetStreamingAsync(
                    "upgrade --all --silent --include-unknown --accept-package-agreements --accept-source-agreements",
                    msg => Log(msg));
                if (code == 0)
                    Log("✅ Обновление всех приложений завершено");
                else if (code == 3010)
                    Log("✅ Обновление завершено. Для применения некоторых обновлений требуется перезагрузка.");
                else
                    Log($"⚠ winget завершился с кодом {code}");
            }
            catch (Exception ex)
            {
                Log($"❌ Ошибка обновления: {ex.Message}");
            }
            finally
            {
                btnUpgradeAll.IsEnabled = true;
                btnRefresh.IsEnabled = true;
                // Обновляем список установленных приложений после завершения
                await LoadAppsAsync();
            }
        }

        // Запуск winget с построчным выводом прогресса в лог
        private async Task<int> RunWingetStreamingAsync(string args, Action<string> onLine)
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "winget",
                Arguments              = args,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("winget не найден");
            // Читаем stderr параллельно, чтобы не было дедлока при переполнении буфера
            var stderrTask = Task.Run(() => p.StandardError.ReadToEnd());

            string? raw;
            string last = "";
            while ((raw = await p.StandardOutput.ReadLineAsync()) != null)
            {
                string clean = StripAnsi(raw).Trim();
                if (string.IsNullOrWhiteSpace(clean)) continue;
                // Пропускаем строки прогресс-бара (только псевдографика/проценты/размеры)
                if (clean.All(c => c is '-' or '─' or '█' or '▒' or '░' or '\\' or '|'
                                     or '/' or '%' or ' ' or '.' or 'K' or 'M' or 'B' or 'G'))
                    continue;
                if (clean == last) continue; // не дублируем одинаковые строки
                last = clean;
                onLine(clean);
            }

            await p.WaitForExitAsync();
            string stderrOutput = await stderrTask;
            if (p.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderrOutput))
                onLine($"[stderr] {stderrOutput.Trim().Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? ""}");
            return p.ExitCode;
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
            if (((Button)sender).Tag is not InstalledApp app) return;
            await UpdateAppAsync(app);
        }

        private async void BtnUninstall_Click(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).Tag is not InstalledApp app) return;

            var res = MessageBox.Show(
                $"Удалить «{app.Name}»?",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            await UninstallAppAsync(app);
        }

        private async void BtnUpdateSelected_Click(object sender, RoutedEventArgs e)
        {
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
            btnUpdateSelected.IsEnabled = true;
        }

        // ── Операции winget ────────────────────────────────────────────────────

        private async Task UpdateAppAsync(InstalledApp app)
        {
            app.IsProcessing = true;
            Log($"⬆ Обновление {app.Name}...");
            try
            {
                // --locale en-US делает вывод английским; русские варианты — на случай
                // если локаль не применилась (старый winget).
                string args = $"upgrade --id \"{app.WingetId}\" --silent --accept-package-agreements --accept-source-agreements --locale en-US";
                string output = await RunWingetAsync(args);
                if (output.Contains("Successfully installed") || output.Contains("No applicable update") ||
                    output.Contains("No installed package found matching") ||
                    output.Contains("Успешно установлено") || output.Contains("Обновления не найдены") ||
                    output.Contains("Не найдено применимых обновлений") || output.Contains("Нет применимых обновлений"))
                {
                    app.Available = "";
                    Dispatcher.Invoke(() => { ApplyFilter(); UpdateStats(); });
                    Log($"✅ {app.Name} обновлён");
                }
                else
                {
                    Log($"⚠ {app.Name}: {output.Trim().Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "нет вывода"}");
                }
            }
            catch (Exception ex) { Log($"❌ {app.Name}: {ex.Message}"); }
            finally { app.IsProcessing = false; }
        }

        private async Task UninstallAppAsync(InstalledApp app)
        {
            app.IsProcessing = true;
            Log($"🗑 Удаление {app.Name}...");
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
            finally { app.IsProcessing = false; }
        }

        private static async Task<bool> TryUninstallAsync(InstalledApp app)
        {
            // Попытка 1: winget uninstall по ID (работает для пакетов с непустым Source)
            if (!string.IsNullOrWhiteSpace(app.WingetId) && !app.WingetId.Contains('…'))
            {
                string args = $"uninstall --id \"{app.WingetId}\" --silent --accept-source-agreements";
                string output = await RunWingetAsync(args);
                if (output.Contains("Successfully uninstalled") || output.Contains("Успешно удалено"))
                    return true;
            }

            // Попытка 2: найти строку UninstallString в реестре по DisplayName
            string? uninstallString = FindUninstallString(app.Name);
            if (uninstallString != null)
                return RunUninstallString(uninstallString);

            return false;
        }

        private static string? FindUninstallString(string displayName)
        {
            string[] keys = {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            var hives = new[] { Registry.LocalMachine, Registry.CurrentUser };

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

        private static bool RunUninstallString(string uninstallString)
        {
            // Тихий режим: msiexec /x ... /quiet, NSIS /S, Inno /SILENT
            string cmd = uninstallString.Trim();
            Process? p;
            if (cmd.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase) ||
                cmd.StartsWith("msiexec", StringComparison.OrdinalIgnoreCase))
            {
                cmd = Regex.Replace(cmd, @"/I\{", "/X{", RegexOptions.IgnoreCase);
                if (!cmd.Contains("/quiet", StringComparison.OrdinalIgnoreCase))
                    cmd += " /quiet /norestart";
                p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c {cmd}")
                    { UseShellExecute = true, Verb = "runas", CreateNoWindow = true });
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
            bool exited = p?.WaitForExit(120_000) ?? false;
            if (!exited) { try { p?.Kill(); } catch { } }
            // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED — удаление прошло успешно
            bool success = exited && (p?.ExitCode == 0 || p?.ExitCode == 3010);
            return success;
        }

        private static async Task<string> RunWingetAsync(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "winget",
                Arguments              = args,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("winget не найден");
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(120));
            var outputTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = Task.Run(() => p.StandardError.ReadToEnd());
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(); } catch { }
            }
            string output = await outputTask;
            await stderrTask;
            return output;
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

        private void Log(string msg) => LogMessage?.Invoke(msg);

        // ── Групповое удаление ────────────────────────────────────────────────

        private async void BtnUninstallSelected_Click(object sender, RoutedEventArgs e)
        {
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
                string output = await RunWingetAsync($"export -o \"{dlg.FileName}\" --accept-source-agreements");
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
                string output = await RunWingetAsync($"import -i \"{dlg.FileName}\" --accept-package-agreements --accept-source-agreements");
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
