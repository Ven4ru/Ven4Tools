using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class SystemTab : UserControl
    {
        private const string TurboBoostRegPath = @"SYSTEM\ControlSet001\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\be337238-0d82-4146-a960-4f3749d470c7";
        private const string TurboSubgroup = "54533251-82be-4824-96c1-47b60b740d00";
        private const string TurboSetting  = "be337238-0d82-4146-a960-4f3749d470c7";
        

        private bool _initialized = false;
        private bool _connSubscribed = false;
        private CancellationTokenSource? _cacheCts;
        private List<CacheAppItem> _cacheAppItems = new();

        private sealed class CacheAppItem
        {
            public string Id          { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public bool   IsSelected  { get; set; }
            public bool   HasDirectUrl { get; set; }
            public string DownloadUrl { get; set; } = "";
            public string WingetId    { get; set; } = "";
        }

        public SystemTab()
        {
            InitializeComponent();

            Loaded += SystemTab_Loaded;

            chkMinimizeToTray.IsChecked = ProfileService.Current.MinimizeToTray;
            chkNotifications.Click += (_, _) => SaveSettings();
            chkUpdateNotifications.Click += (_, _) => SaveSettings();
            sliderCatalogTimeout.ValueChanged += (_, e) => { txtCatalogTimeout.Text = $"{(int)e.NewValue} сек"; SaveSettings(); };
            sliderCheckTimeout.ValueChanged += (_, e) => { txtCheckTimeout.Text = $"{(int)e.NewValue} сек"; SaveSettings(); };
            btnCopySystemInfo.Click += BtnCopySystemInfo_Click;
            btnOpenLogs.Click += BtnOpenLogs_Click;
            btnOpenLatestLog.Click += BtnOpenLatestLog_Click;
            btnClearLogs.Click += BtnClearLogs_Click;
            btnCheckUpdates.Click += BtnCheckUpdates_Click;
            btnDisableTurboBoost.Click += BtnDisableTurboBoost_Click;
            btnEnableTurboBoost.Click += BtnEnableTurboBoost_Click;

            // Offline mode
            chkOfflineMode.Click      += ChkOfflineMode_Click;
            txtOfflineCachePath.LostFocus += (_, _) => SaveOfflineSettings();

            // Подписка на ConnectivityMonitor — в Loaded: вкладка кэшируется и переиспользуется,
            // поэтому после Unloaded нужно подписываться заново при каждом показе
            Unloaded += SystemTab_Unloaded;

            LoadSettings();
            LoadOfflineSettings();
        }

        private void OnConnectivityChanged(bool online) => Dispatcher.Invoke(UpdateConnectivityStatus);

        private void SystemTab_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_connSubscribed)
            {
                ConnectivityMonitor.StatusChanged -= OnConnectivityChanged;
                _connSubscribed = false;
            }
        }


        private async void SystemTab_Loaded(object sender, RoutedEventArgs e)
        {
            // Переподписка при каждом показе вкладки (после Unloaded подписка снимается)
            if (!_connSubscribed)
            {
                ConnectivityMonitor.StatusChanged += OnConnectivityChanged;
                _connSubscribed = true;
            }
            UpdateConnectivityStatus();

            if (_initialized) return;
            _initialized = true;

            LoadSystemInfo();
            LoadSourceOrderUI();
            UpdateCacheStats();
            LoadCacheAppsList();

            bool? turbo = await GetTurboBoostStateAsync();
            if (turbo.HasValue)
                AddLog(turbo.Value ? "⚡ Турбобуст: включён" : "⚡ Турбобуст: отключён");
        }
        
        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "settings.json");

        private void LoadSettings()
        {
            // AppSettings is already loaded from the same file at startup
            chkNotifications.IsChecked = AppSettings.Notifications;
            chkUpdateNotifications.IsChecked = AppSettings.UpdateNotifications;
            sliderCatalogTimeout.Value = Math.Clamp(AppSettings.CatalogTimeout, 3, 30);
            sliderCheckTimeout.Value = Math.Clamp(AppSettings.CheckTimeout, 5, 60);
            txtCatalogTimeout.Text = $"{(int)sliderCatalogTimeout.Value} сек";
            txtCheckTimeout.Text = $"{(int)sliderCheckTimeout.Value} сек";
        }

        private void SaveSettings()
        {
            try
            {
                var settings = new
                {
                    Notifications = chkNotifications.IsChecked ?? true,
                    UpdateNotifications = chkUpdateNotifications.IsChecked ?? true,
                    CatalogTimeout = (int)sliderCatalogTimeout.Value,
                    CheckTimeout = (int)sliderCheckTimeout.Value
                };
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, Newtonsoft.Json.JsonConvert.SerializeObject(settings, Newtonsoft.Json.Formatting.Indented));
                AppSettings.NotifyChanged();
            }
            catch { }
        }
        
        private void LoadSystemInfo()
        {
            try
            {
                txtOSVersion.Text = Environment.OSVersion.VersionString;
                
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        txtProcessor.Text = obj["Name"]?.ToString()?.Trim() ?? "Неизвестно";
                        break;
                    }
                }
                
                using (var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        long totalMemory = Convert.ToInt64(obj["TotalVisibleMemorySize"]) / 1024 / 1024;
                        txtRAM.Text = $"{totalMemory} ГБ";
                        break;
                    }
                }
                
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                txtAppVersion.Text = version?.ToString() ?? "—";
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка загрузки информации о системе: {ex.Message}");
            }
        }
        
        private void ChkMinimizeToTray_Click(object sender, RoutedEventArgs e)
        {
            ProfileService.Current.MinimizeToTray = chkMinimizeToTray.IsChecked == true;
            ProfileService.Save();
        }

        private void BtnCopySystemInfo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string info = $"ОС: {txtOSVersion.Text}\n" +
                              $"Процессор: {txtProcessor.Text}\n" +
                              $"ОЗУ: {txtRAM.Text}\n" +
                              $"Ven4Tools: {txtAppVersion.Text}";
                
                Clipboard.SetText(info);
                AddLog("📋 Информация о системе скопирована в буфер обмена");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка копирования: {ex.Message}");
            }
        }
        
        private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "logs");
                Directory.CreateDirectory(logsPath);
                Process.Start("explorer.exe", logsPath);
                AddLog($"📁 Открыта папка логов: {logsPath}");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка открытия папки логов: {ex.Message}");
            }
        }

        private void BtnOpenLatestLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "logs");
                if (!Directory.Exists(logsPath)) { AddLog("📋 Логов нет"); return; }

                var latestLog = Directory.GetFiles(logsPath, "install_*.log")
                    .OrderByDescending(f => f)
                    .FirstOrDefault();

                if (latestLog == null) { AddLog("📋 Файлы логов не найдены"); return; }

                var lines = File.ReadAllLines(latestLog);
                var preview = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 50)));
                txtLatestLog.Text = preview;

                Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = latestLog, UseShellExecute = true });
                AddLog($"📄 Открыт лог: {Path.GetFileName(latestLog)}");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка: {ex.Message}");
            }
        }

        private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            btnCheckUpdates.IsEnabled = false;
            txtUpdatesLog.Text = "⏳ Проверка...";
            try
            {
                var (_, raw) = await WingetRunner.RunAsync(
                    "upgrade --include-unknown --source winget",
                    TimeSpan.FromMinutes(3));

                var upgradable = raw.Split('\n')
                    .Select(l => WingetRunner.StripAnsi(l).Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l)
                             && !l.StartsWith("Name")
                             && !l.StartsWith("-")
                             && !l.StartsWith("The ")
                             && l.Contains("  "))
                    .ToList();

                if (upgradable.Count > 0)
                {
                    txtUpdatesLog.Text = $"🔔 Доступно обновлений: {upgradable.Count}\n\n" + string.Join("\n", upgradable);
                    AddLog($"🔔 Доступно обновлений winget: {upgradable.Count}");
                }
                else
                {
                    txtUpdatesLog.Text = "✅ Все установленные приложения актуальны";
                    AddLog("✅ Обновлений winget не найдено");
                }
            }
            catch (Exception ex)
            {
                txtUpdatesLog.Text = $"❌ Ошибка: {ex.Message}";
                AddLog($"❌ Ошибка проверки обновлений: {ex.Message}");
            }
            finally
            {
                btnCheckUpdates.IsEnabled = true;
            }
        }
        
        private void BtnClearLogs_Click(object sender, RoutedEventArgs e)
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
                        AddLog("🗑️ Логи очищены");
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"❌ Ошибка очистки логов: {ex.Message}");
                }
            }
        }
        
        private async void BtnDisableTurboBoost_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await ApplyTurboBoostAsync(false);
                AddLog("⚡ Турбобуст отключён");
                MessageBox.Show("✅ Турбобуст отключён.\nИзменение применено немедленно — перезагрузка не требуется.",
                    "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка при отключении турбобуста: {ex.Message}");
                MessageBox.Show("Не удалось отключить турбобуст. Запустите приложение от имени администратора и попробуйте ещё раз.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnEnableTurboBoost_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await ApplyTurboBoostAsync(true);
                AddLog("⚡ Турбобуст включён");
                MessageBox.Show("✅ Турбобуст включён.\nИзменение применено немедленно — перезагрузка не требуется.",
                    "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка при включении турбобуста: {ex.Message}");
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
                    FileName = "powercfg",
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
            catch { }
            return null;
        }

        private async Task RunPowerCfgAsync(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi) ?? throw new Exception("Не удалось запустить powercfg");
            // Читаем stderr асинхронно — иначе WaitForExit зависнет если буфер stderr переполнится.
            // WaitForExitAsync не блокирует UI-поток.
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
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
            catch { }
        }
        
        // ── Source order ──────────────────────────────────────────────────────────

        private sealed class SourceItem
        {
            public string Id    { get; set; } = "";
            public string Label { get; set; } = "";
        }

        private System.Collections.ObjectModel.ObservableCollection<SourceItem> _sourceItems = new();

        private void LoadSourceOrderUI()
        {
            var settings = SourceOrderService.Current;
            rbSourceGlobal.IsChecked      = settings.Mode == "global";
            rbSourcePerCategory.IsChecked = settings.Mode == "per_category";

            _sourceItems.Clear();
            foreach (var id in settings.GlobalOrder)
                _sourceItems.Add(new SourceItem { Id = id, Label = SourceOrderSettings.Labels.GetValueOrDefault(id, id) });

            lstSourceOrder.ItemsSource = _sourceItems;
            UpdateSourcePanels();
        }

        private void UpdateSourcePanels()
        {
            bool isGlobal = rbSourceGlobal.IsChecked == true;
            pnlGlobalOrder.Visibility      = isGlobal ? Visibility.Visible : Visibility.Collapsed;
            pnlPerCategoryHint.Visibility  = isGlobal ? Visibility.Collapsed : Visibility.Visible;
        }

        private void RbSourceMode_Click(object sender, RoutedEventArgs e) => UpdateSourcePanels();

        private void BtnSrcUp_Click(object sender, RoutedEventArgs e)
        {
            int idx = lstSourceOrder.SelectedIndex;
            if (idx <= 0) return;
            _sourceItems.Move(idx, idx - 1);
            lstSourceOrder.SelectedIndex = idx - 1;
        }

        private void BtnSrcDown_Click(object sender, RoutedEventArgs e)
        {
            int idx = lstSourceOrder.SelectedIndex;
            if (idx < 0 || idx >= _sourceItems.Count - 1) return;
            _sourceItems.Move(idx, idx + 1);
            lstSourceOrder.SelectedIndex = idx + 1;
        }

        private void BtnSaveSourceOrder_Click(object sender, RoutedEventArgs e)
        {
            SourceOrderService.Current.Mode        = rbSourceGlobal.IsChecked == true ? "global" : "per_category";
            SourceOrderService.Current.GlobalOrder = _sourceItems.Select(i => i.Id).ToList();
            SourceOrderService.Save();

            txtSourceOrderStatus.Text = $"✅ Сохранено {DateTime.Now:HH:mm:ss} · Запуск проверки доступности...";
            AddLog("🔀 Порядок источников сохранён");
        }

        // ── Offline mode ──────────────────────────────────────────────────────────

        private void LoadOfflineSettings()
        {
            chkOfflineMode.IsChecked  = ProfileService.Current.OfflineMode;
            txtOfflineCachePath.Text  = ProfileService.Current.OfflineCachePath;
            if (string.IsNullOrEmpty(txtOfflineCachePath.Text))
                txtOfflineCachePath.Text = OfflineService.CachePath;
        }

        private void SaveOfflineSettings()
        {
            ProfileService.Current.OfflineCachePath = txtOfflineCachePath.Text.Trim();
            ProfileService.Save();
        }

        private void ChkOfflineMode_Click(object sender, RoutedEventArgs e)
        {
            ProfileService.Current.OfflineMode = chkOfflineMode.IsChecked == true;
            ProfileService.Save();
            // Notify MainWindow to update tab visibility
            if (Window.GetWindow(this) is MainWindow mw)
                mw.UpdateTabVisibility();
            UpdateConnectivityStatus();
        }

        private void UpdateConnectivityStatus()
        {
            bool online = ConnectivityMonitor.IsOnline;
            bool forced = ProfileService.Current.OfflineMode;

            if (!online)
            {
                txtConnIcon.Text   = "🔴";
                txtConnStatus.Text = "Интернет недоступен — онлайн-вкладки скрыты";
                pnlConnStatus.Background = new SolidColorBrush(Color.FromRgb(80, 20, 20));
            }
            else if (forced)
            {
                txtConnIcon.Text   = "🟡";
                txtConnStatus.Text = "Принудительный офлайн — вкладки скрыты вручную";
                pnlConnStatus.Background = new SolidColorBrush(Color.FromRgb(70, 55, 10));
            }
            else
            {
                txtConnIcon.Text   = "🟢";
                txtConnStatus.Text = "Интернет доступен — все вкладки активны";
                pnlConnStatus.Background = new SolidColorBrush(Color.FromRgb(15, 50, 20));
            }
        }

        private void UpdateCacheStats()
        {
            var (count, sizeMB) = OfflineService.GetCacheStats();
            txtCacheStats.Text = count == 0
                ? "Кэш пуст"
                : $"{count} файлов · {sizeMB} МБ  ({OfflineService.CachePath})";
        }

        private void LoadCacheAppsList()
        {
            var catalog = CatalogLoaderService.LoadedCatalog;
            if (catalog == null || catalog.Apps.Count == 0)
            {
                listCacheApps.ItemsSource = null;
                return;
            }

            _cacheAppItems = catalog.Apps
                .Where(a => !string.IsNullOrEmpty(a.DownloadUrl) || !string.IsNullOrEmpty(a.WingetId))
                .OrderBy(a => a.Name)
                .Select(a => new CacheAppItem
                {
                    Id           = a.Id,
                    DisplayName  = $"{a.Name}  [{a.Category}]{(OfflineService.HasCachedInstaller(a.Id) ? " ✅" : "")}",
                    HasDirectUrl = !string.IsNullOrEmpty(a.DownloadUrl),
                    DownloadUrl  = a.DownloadUrl,
                    WingetId     = a.WingetId
                })
                .ToList();

            listCacheApps.ItemsSource = _cacheAppItems;
        }

        private void TxtCacheAppFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            string q = txtCacheAppFilter.Text.Trim().ToLowerInvariant();
            listCacheApps.ItemsSource = string.IsNullOrEmpty(q)
                ? _cacheAppItems
                : _cacheAppItems.Where(a => a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private void BtnCacheSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _cacheAppItems) item.IsSelected = true;
            listCacheApps.Items.Refresh();
        }

        private void BtnCacheSelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _cacheAppItems) item.IsSelected = false;
            listCacheApps.Items.Refresh();
        }

        private void BtnBrowseCachePath_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description      = "Выберите папку для кэша установщиков",
                ShowNewFolderButton = true
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                txtOfflineCachePath.Text = dlg.SelectedPath;
                ProfileService.Current.OfflineCachePath = dlg.SelectedPath;
                ProfileService.Save();
                UpdateCacheStats();
            }
        }

        private void BtnOpenCacheFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                OfflineService.EnsureCacheDir();
                Process.Start(new ProcessStartInfo(OfflineService.CachePath) { UseShellExecute = true });
            }
            catch (Exception ex) { AddLog($"❌ {ex.Message}"); }
        }

        private void BtnClearCache_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show("Удалить все кэшированные установщики?",
                "Очистка кэша", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            OfflineService.ClearCache();
            UpdateCacheStats();
            LoadCacheAppsList();
            AddLog("✅ Кэш очищен");
        }

        private async void BtnDownloadToCache_Click(object sender, RoutedEventArgs e)
        {
            var selected = _cacheAppItems.Where(a => a.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Не выбрано ни одного приложения.", "Нет выбора",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _cacheCts = new CancellationTokenSource();
            var token = _cacheCts.Token;

            btnDownloadToCache.IsEnabled       = false;
            btnCancelCacheDownload.Visibility  = Visibility.Visible;
            progressCache.Visibility           = Visibility.Visible;
            txtCacheLog.Visibility             = Visibility.Visible;
            txtCacheLog.Clear();

            // Вся подготовка — внутри try: исключение в async void-обработчике
            // (например, недопустимый путь кэша в EnsureCacheDir) уронило бы всё приложение
            try
            {
                SaveOfflineSettings();
                OfflineService.EnsureCacheDir();

                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
                http.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools");

                int done = 0, total = selected.Count, errors = 0;

                foreach (var item in selected)
                {
                    if (token.IsCancellationRequested) break;

                    // Минимальный объект App для сервиса
                    var app = new Ven4Tools.Models.App
                    {
                        Id          = item.Id,
                        Name        = item.DisplayName.Split('[')[0].Trim().TrimEnd(' ', '✅').Trim(),
                        DownloadUrl = item.DownloadUrl,
                        WingetId    = item.WingetId
                    };

                    var progress = new Progress<(string status, int pct)>(v =>
                    {
                        if (v.pct >= 0) progressCache.Value = v.pct;
                        if (!v.status.StartsWith("  ")) // пропускаем подробные строки winget
                            txtCacheLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {v.status}\n");
                        txtCacheLog.ScrollToEnd();
                    });

                    try
                    {
                        bool ok = item.HasDirectUrl
                            ? await OfflineService.CacheInstallerDirectAsync(app, http, progress, token)
                            : await OfflineService.CacheInstallerWingetAsync(app, progress, token);

                        if (!ok) errors++;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        txtCacheLog.AppendText($"❌ {app.Name}: {ex.Message}\n");
                        errors++;
                    }

                    done++;
                    progressCache.Value = (double)done / total * 100;
                }

                string summary = token.IsCancellationRequested
                    ? $"⏹ Остановлено. Скачано: {done}/{total}"
                    : $"✅ Готово: {done}/{total}{(errors > 0 ? $", ошибок: {errors}" : "")}";
                txtCacheLog.AppendText($"\n{summary}\n");
                txtCacheLog.ScrollToEnd();
                AddLog(summary);
            }
            catch (Exception ex)
            {
                txtCacheLog.AppendText($"❌ Ошибка: {ex.Message}\n");
                AddLog($"❌ Ошибка кэширования: {ex.Message}");
            }
            finally
            {
                btnDownloadToCache.IsEnabled      = true;
                btnCancelCacheDownload.Visibility = Visibility.Collapsed;
                btnCancelCacheDownload.IsEnabled  = true;
                progressCache.Value = 0;
                UpdateCacheStats();
                LoadCacheAppsList();

                _cacheCts.Dispose();
                _cacheCts = null;
            }
        }

        private void BtnCancelCacheDownload_Click(object sender, RoutedEventArgs e)
        {
            _cacheCts?.Cancel();
            btnCancelCacheDownload.IsEnabled = false;
        }

        // ─────────────────────────────────────────────────────────────────────────

        private static void AddLog(string message) => AppLogger.Write(message);
    }
}


