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
            rdbUpdates.IsChecked = true;
            ApplyFilter();
        }

        // ── Загрузка ────────────────────────────────────────────────────────────

        private async Task LoadAppsAsync()
        {
            ShowState("loading");
            txtLoadingMsg.Text = "⏳ Получение списка установленных приложений...";

            try
            {
                _allApps = await Task.Run(() => RunWingetList());
                ApplyFilter();
                ShowState(_allApps.Count == 0 ? "empty" : "list");
                UpdateStats();
            }
            catch (Exception ex)
            {
                txtLoadingMsg.Text = $"❌ Ошибка: {ex.Message}";
            }
        }

        private static List<InstalledApp> RunWingetList()
        {
            var output = RunWinget("list --accept-source-agreements");
            return ParseWingetList(output);
        }

        private static List<InstalledApp> ParseWingetList(string raw)
        {
            var result = new List<InstalledApp>();
            if (string.IsNullOrWhiteSpace(raw)) return result;

            var lines = raw.Split('\n');

            // Найти строку заголовка (содержит "Name" и "Id")
            int headerIdx = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.Contains("Name") && l.Contains("Id") && l.Contains("Version"))
                {
                    headerIdx = i;
                    break;
                }
            }
            if (headerIdx < 0) return result;

            // Winget склеивает прогресс-бар с заголовком в одну строку — отрезаем префикс
            string header = lines[headerIdx];
            int nameStart = header.IndexOf("Name", StringComparison.Ordinal);
            if (nameStart > 0) header = header.Substring(nameStart);

            // Позиции колонок — индексы слов в строке заголовка
            int colName      = header.IndexOf("Name",      StringComparison.Ordinal);
            int colId        = header.IndexOf("Id",        StringComparison.Ordinal);
            int colVersion   = header.IndexOf("Version",   StringComparison.Ordinal);
            int colAvailable = header.IndexOf("Available", StringComparison.Ordinal);
            int colSource    = header.IndexOf("Source",    StringComparison.Ordinal);

            if (colName < 0 || colId < 0 || colVersion < 0) return result;

            // Пропустить строку-разделитель и парсить данные
            int dataStart = headerIdx + 1;
            if (dataStart < lines.Length && lines[dataStart].TrimStart().StartsWith("-"))
                dataStart++;

            for (int i = dataStart; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.TrimStart().StartsWith("-")) continue;

                string name      = Extract(line, colName,      colId);
                string id        = Extract(line, colId,        colVersion);
                string version   = Extract(line, colVersion,   colAvailable >= 0 ? colAvailable : line.Length);
                string available = colAvailable >= 0 ? Extract(line, colAvailable, colSource >= 0 ? colSource : line.Length) : "";
                string source    = colSource    >= 0 ? Extract(line, colSource,    line.Length) : "";

                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(id)) continue;

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

            if (rdbUpdates.IsChecked == true)
                filtered = filtered.Where(a => a.HasUpdate);
            else if (rdbUnknown.IsChecked == true)
                filtered = filtered.Where(a => a.IsUnknownSource);

            if (!string.IsNullOrEmpty(search))
                filtered = filtered.Where(a =>
                    a.Name.ToLowerInvariant().Contains(search) ||
                    a.WingetId.ToLowerInvariant().Contains(search));

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

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            btnRefresh.IsEnabled = false;
            await LoadAppsAsync();
            btnRefresh.IsEnabled = true;
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
            btnUpdateSelected.IsEnabled = visible?.Any(a => a.IsSelected && a.HasUpdate) == true;
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
                    bool rpOk = await CreateRestorePointAsync();
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
                string args = $"upgrade --id \"{app.WingetId}\" --silent --accept-package-agreements --accept-source-agreements";
                string output = await Task.Run(() => RunWinget(args));
                if (output.Contains("Successfully installed") || output.Contains("No applicable update"))
                {
                    app.Available = "";
                    Dispatcher.Invoke(() => { ApplyFilter(); UpdateStats(); });
                    Log($"✅ {app.Name} обновлён");
                    if (UserSession.IsLoggedIn)
                        await GamificationService.Instance.TrackUpdateAsync();
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
                bool ok = await Task.Run(() => TryUninstall(app));
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

        private static bool TryUninstall(InstalledApp app)
        {
            // Попытка 1: winget uninstall по ID (работает для пакетов с непустым Source)
            if (!string.IsNullOrWhiteSpace(app.WingetId) && !app.WingetId.Contains('…'))
            {
                string args = $"uninstall --id \"{app.WingetId}\" --silent --accept-source-agreements";
                string output = RunWinget(args);
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
            bool success = exited && (p?.ExitCode == 0);
            return success;
        }

        private static string RunWinget(string args)
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
            // Читаем stderr параллельно — иначе дедлок если буфер stderr переполнится
            var stderrTask = Task.Run(() => p.StandardError.ReadToEnd());
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(120_000);
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

        private static async Task<bool> CreateRestorePointAsync()
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"Checkpoint-Computer -Description 'Ven4Tools — перед обновлением' -RestorePointType MODIFY_SETTINGS\"")
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                await Task.WhenAll(
                    p.StandardOutput.ReadToEndAsync(),
                    p.StandardError.ReadToEndAsync());
                await p.WaitForExitAsync();
                return p.ExitCode == 0;
            }
            catch { return false; }
        }
    }
}
