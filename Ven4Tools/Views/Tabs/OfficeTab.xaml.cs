using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class OfficeTab : UserControl
    {
        private static readonly HttpClient _httpClient = CreateHttpClient();
        private CancellationTokenSource? _cancellationTokenSource;
        private string? _downloadedFilePath;

        // Сохранённое состояние региона (Office CC и Windows GeoID)
        private string? _originalOfficeCC;   // исходное значение из ExperimentConfigs\Ecs\CountryCode
        private string? _originalGeoName;    // например "RU" из Control Panel\International\Geo\Name
        private string? _originalGeoNation;  // например "203" из Control Panel\International\Geo\Nation

        public event Action? GoToActivation;

        private readonly string[] officeLanguages = { "ru-ru", "en-us", "de-de", "fr-fr", "es-es", "it-it", "zh-cn", "ja-jp" };

        private readonly Dictionary<string, string> officeDirectLinks = new()
        {
            { "O365ProPlusRetail",       "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=O365ProPlusRetail&platform=x64&language={0}&version=O16GA" },
            { "ProPlus2024Retail",       "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=ProPlus2024Retail&platform=x64&language={0}&version=O16GA" },
            { "Professional2021Retail",  "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=Professional2021Retail&platform=x64&language={0}&version=O16GA" },
            { "Professional2019Retail",  "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=Professional2019Retail&platform=x64&language={0}&version=O16GA" },
            { "ProPlusRetail",           "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=ProPlusRetail&platform=x64&language={0}&version=O16GA" }
        };

        public OfficeTab()
        {
            InitializeComponent();
            FillComboBoxes();

            btnDownloadOffice.Click += BtnDownloadOffice_Click;
            btnInstallOffice.Click  += BtnInstallOffice_Click;
            btnCancelOffice.Click   += (_, _) =>
            {
                _cancellationTokenSource?.Cancel();
                btnCancelOffice.IsEnabled = false;
                AddLog("⏹️ Запрос отмены...");
            };
            btnGoActivation.Click += (_, _) => GoToActivation?.Invoke();

            UserSession.Changed += UpdateActivationPanel;
            Unloaded += (_, _) => UserSession.Changed -= UpdateActivationPanel;
            UpdateActivationPanel();
            UpdateRegionDisplay();
        }

        private void UpdateActivationPanel()
        {
            Dispatcher.Invoke(() =>
                pnlActivationHint.Visibility = UserSession.IsLoggedIn ? Visibility.Visible : Visibility.Collapsed);
        }

        // ── Отображение региона (читаем реестр напрямую — изменения видны сразу) ──

        private void UpdateRegionDisplay()
        {
            Dispatcher.Invoke(() =>
            {
                // Windows GeoID — читаем прямо из реестра, чтобы изменения были видны сразу
                try
                {
                    using var geo = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\Geo");
                    string? name   = geo?.GetValue("Name")?.ToString();
                    string? nation = geo?.GetValue("Nation")?.ToString();
                    txtRegionGeo.Text = (name, nation) switch
                    {
                        ({ } n, { } id) => $"{n} (GeoID: {id})",
                        ({ } n, _)      => n,
                        (_, { } id)     => $"GeoID: {id}",
                        _               => "недоступен"
                    };
                }
                catch { txtRegionGeo.Text = "ошибка чтения"; }

                // Office CountryCode
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs");
                    string? raw = key?.GetValue("CountryCode")?.ToString();
                    txtRegionCC.Text = raw == null
                        ? "не задан"
                        : raw.StartsWith("std::wstring|") ? raw["std::wstring|".Length..] : raw;
                }
                catch { txtRegionCC.Text = "недоступен"; }
            });
        }

        // ── Сохранение / смена / восстановление региона ───────────────────────

        private void SaveRegion()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs");
                _originalOfficeCC = key?.GetValue("CountryCode")?.ToString();
            }
            catch { _originalOfficeCC = null; }

            try
            {
                using var geo = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\Geo");
                _originalGeoName   = geo?.GetValue("Name")?.ToString();
                _originalGeoNation = geo?.GetValue("Nation")?.ToString();
            }
            catch { _originalGeoName = _originalGeoNation = null; }
        }

        private void SetRegionUS()
        {
            // Office ExperimentConfigs CountryCode
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs");
                key?.SetValue("CountryCode", "std::wstring|US", RegistryValueKind.String);
            }
            catch (Exception ex) { AddLog($"⚠️ Office CountryCode: {ex.Message}"); }

            // Windows GeoID (Name = код ISO-3166 alpha-2, Nation = числовой GeoID)
            try
            {
                using var geo = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\Geo", writable: true);
                if (geo != null)
                {
                    geo.SetValue("Name",   "US",  RegistryValueKind.String);
                    geo.SetValue("Nation", "244", RegistryValueKind.String);
                }
                else
                    AddLog("⚠️ Control Panel\\International\\Geo — ключ не найден");
            }
            catch (Exception ex) { AddLog($"⚠️ Windows GeoID: {ex.Message}"); }

            UpdateRegionDisplay();
        }

        private void RestoreRegion()
        {
            // Office CountryCode
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs", writable: true);
                if (key != null)
                {
                    if (_originalOfficeCC != null)
                        key.SetValue("CountryCode", _originalOfficeCC, RegistryValueKind.String);
                    else
                        key.DeleteValue("CountryCode", throwOnMissingValue: false);
                }
            }
            catch (Exception ex) { AddLog($"⚠️ Восстановление Office CC: {ex.Message}"); }

            // Windows GeoID
            try
            {
                using var geo = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\Geo", writable: true);
                if (geo != null)
                {
                    if (_originalGeoName != null)
                        geo.SetValue("Name", _originalGeoName, RegistryValueKind.String);

                    if (_originalGeoNation != null)
                        geo.SetValue("Nation", _originalGeoNation, RegistryValueKind.String);
                    else
                        geo.DeleteValue("Nation", throwOnMissingValue: false);
                }
            }
            catch (Exception ex) { AddLog($"⚠️ Восстановление Windows GeoID: {ex.Message}"); }

            UpdateRegionDisplay();
        }

        // ── Скачивание ────────────────────────────────────────────────────────

        private async void BtnDownloadOffice_Click(object sender, RoutedEventArgs e)
        {
            if (cmbOfficeLanguage.SelectedItem == null) return;

            var (displayName, productId) = GetSelectedVersion();
            string lang = cmbOfficeLanguage.SelectedItem.ToString()!;

            // Удаляем предыдущий скачанный установщик, если он остался
            if (_downloadedFilePath != null)
            {
                try { File.Delete(_downloadedFilePath); } catch { }
                _downloadedFilePath = null;
            }

            btnDownloadOffice.IsEnabled = false;
            btnInstallOffice.IsEnabled  = false;
            btnCancelOffice.IsEnabled   = true;

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            SetProgress(true, "⏳ Подготовка...", 0, "");
            AddLog($"\n📥 Скачивание {displayName} ({lang})...");

            string tempFile = Path.Combine(Path.GetTempPath(), $"OfficeSetup_{Guid.NewGuid():N}.exe");

            try
            {
                string downloadUrl = string.Format(officeDirectLinks[productId], lang);
                using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();

                using var src = await response.Content.ReadAsStreamAsync();
                using var dst = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);
                var  buf      = new byte[65536];
                int  read;
                long total    = 0;
                long? size    = response.Content.Headers.ContentLength;
                int  lastPct  = -1;

                while ((read = await src.ReadAsync(buf, token)) > 0)
                {
                    await dst.WriteAsync(buf, 0, read, token);
                    total += read;

                    if (size.HasValue)
                    {
                        int pct = (int)(total * 100.0 / size.Value);
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            SetProgress(true,
                                $"📥 Скачивание: {pct}%", pct,
                                $"{(double)total / 1_048_576:F1} / {(double)size.Value / 1_048_576:F1} МБ");
                        }
                    }
                    else
                    {
                        SetProgress(true, "📥 Скачивание...", 0,
                            $"{(double)total / 1_048_576:F1} МБ");
                    }
                }

                var fi = new FileInfo(tempFile);
                AddLog($"✅ Скачано: {fi.Length / 1_048_576.0:F1} МБ");
                SetProgress(true, "✅ Скачано! Нажмите «Установить»", 100,
                    $"{fi.Length / 1_048_576.0:F1} МБ");

                _downloadedFilePath = tempFile;
                btnInstallOffice.IsEnabled = true;
            }
            catch (OperationCanceledException)
            {
                AddLog("⏹️ Скачивание отменено");
                SetProgress(true, "⏹️ Отменено", 0, "");
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка скачивания: {ex.Message}");
                SetProgress(true, "❌ Ошибка", 0, "");
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                MessageBox.Show("Не удалось скачать Office. Проверьте подключение к интернету и попробуйте ещё раз.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                Dispatcher.Invoke(() =>
                {
                    btnDownloadOffice.IsEnabled = true;
                    btnCancelOffice.IsEnabled   = false;
                });
            }
        }

        // ── Установка ─────────────────────────────────────────────────────────

        private async void BtnInstallOffice_Click(object sender, RoutedEventArgs e)
        {
            if (_downloadedFilePath == null || !File.Exists(_downloadedFilePath))
            {
                AddLog("⚠️ Файл установщика не найден — скачайте снова.");
                btnInstallOffice.IsEnabled = false;
                return;
            }

            var (displayName, _) = GetSelectedVersion();

            btnInstallOffice.IsEnabled  = false;
            btnDownloadOffice.IsEnabled = false;
            btnCancelOffice.IsEnabled   = true;

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;
            bool regionChanged = false;

            SetProgress(true, "⏳ Подготовка установки...", 0, "");
            AddLog($"\n🚀 Установка {displayName}...");

            try
            {
                SaveRegion();
                regionChanged = true; // до SetRegionUS — чтобы finally откатил даже при исключении внутри
                SetRegionUS();
                AddLog("🌎 Регион переключён на US (GeoID: 244, CountryCode: US)");

                SetPhase("🚀 Запуск установщика...");
                var existingPids = GetC2RProcessPids();

                using var bootstrapper = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo
                    {
                        FileName        = _downloadedFilePath,
                        UseShellExecute = true,
                        Verb            = "runas"
                    });

                if (bootstrapper != null)
                {
                    await bootstrapper.WaitForExitAsync(token);
                    if (bootstrapper.ExitCode != 0)
                    {
                        AddLog($"❌ Установщик завершился с кодом {bootstrapper.ExitCode}");
                        AddLog("   Вероятная причина: CDN Microsoft заблокирован в вашем регионе.");
                        AddLog("   Попробуйте использовать VPN и повторить установку.");
                        SetProgress(true, $"❌ Сбой установки (код {bootstrapper.ExitCode})", 0,
                            "CDN Microsoft может быть недоступен. Попробуйте VPN.");
                        return;
                    }
                }

                token.ThrowIfCancellationRequested();

                SetPhase("⚙️ Установка Office... не закрывайте приложение");
                AddLog("⏳ Ожидаем запуск C2R-установщика...");

                var installProc = await WaitForC2RProcess(existingPids, TimeSpan.FromMinutes(3), token);

                if (installProc == null)
                {
                    AddLog("⚠️ Процесс установки не обнаружен — возможно Office уже установлен или завершился мгновенно");
                }
                else
                {
                    AddLog($"🔍 Мониторинг: {installProc.ProcessName} (PID {installProc.Id})");
                    SetProgress(true, "⚙️ Установка Office...", 0, "Идёт установка, пожалуйста подождите...");
                    progressOffice.IsIndeterminate = true;
                    await MonitorInstallation(installProc, token);
                    progressOffice.IsIndeterminate = false;
                }

                token.ThrowIfCancellationRequested();

                RestoreRegion();
                regionChanged = false;
                AddLog("✅ Установка завершена — регион восстановлен");
                SetProgress(true, "✅ Офис установлен!", 100, "Регион восстановлен");

                if (chkSaveInstaller.IsChecked != true)
                {
                    try { File.Delete(_downloadedFilePath); } catch { }
                    _downloadedFilePath = null;
                }
            }
            catch (OperationCanceledException)
            {
                AddLog("⏹️ Установка отменена");
                SetProgress(true, "⏹️ Отменено", 0, "");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка установки: {ex.Message}");
                SetProgress(true, "❌ Ошибка установки", 0, "");
                MessageBox.Show("Не удалось установить Office. Попробуйте ещё раз или установите вручную.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (regionChanged)
                {
                    RestoreRegion();
                    AddLog("🔁 Регион восстановлен (аварийный сброс)");
                }
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                Dispatcher.Invoke(() =>
                {
                    btnDownloadOffice.IsEnabled = true;
                    btnCancelOffice.IsEnabled   = false;
                    btnInstallOffice.IsEnabled  = _downloadedFilePath != null && File.Exists(_downloadedFilePath);
                });
            }
        }

        // ── Помощники для процессов C2R ───────────────────────────────────────

        private static HashSet<int> GetC2RProcessPids()
        {
            var names = new[] { "officec2rclient", "OfficeClickToRun" };
            var pids  = new HashSet<int>();
            foreach (var name in names)
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                    pids.Add(p.Id);
            return pids;
        }

        private static async Task<System.Diagnostics.Process?> WaitForC2RProcess(
            HashSet<int> existingPids, TimeSpan timeout, CancellationToken token)
        {
            var deadline = DateTime.UtcNow + timeout;
            var names    = new[] { "officec2rclient", "OfficeClickToRun" };

            while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            {
                foreach (var name in names)
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                        if (!existingPids.Contains(p.Id))
                            return p;

                await Task.Delay(2000, token);
            }
            return null;
        }

        private async Task MonitorInstallation(System.Diagnostics.Process proc, CancellationToken token)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(60);
            var elapsed  = System.Diagnostics.Stopwatch.StartNew();

            while (!proc.HasExited && DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            {
                await Task.Delay(5000, token);
                SetDetail($"Установка идёт {elapsed.Elapsed:mm\\:ss}...");
            }

            if (!proc.HasExited)
                AddLog("⚠️ Таймаут ожидания — продолжаем без подтверждения");
        }

        // ── Вспомогательные методы ────────────────────────────────────────────

        private (string DisplayName, string ProductId) GetSelectedVersion()
        {
            if (rdbO2024.IsChecked == true) return ("Office 2024 ProPlus",     "ProPlus2024Retail");
            if (rdbO2021.IsChecked == true) return ("Office 2021 Professional", "Professional2021Retail");
            if (rdbO2019.IsChecked == true) return ("Office 2019 Professional", "Professional2019Retail");
            if (rdbO2016.IsChecked == true) return ("Office 2016 Professional", "ProPlusRetail");
            return ("Office 365 ProPlus", "O365ProPlusRetail");
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            var client = new HttpClient(handler);
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0");
            client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            client.DefaultRequestHeaders.Add("Accept-Language", "ru-RU,ru;q=0.9,en;q=0.8");
            client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            client.DefaultRequestHeaders.Add("Connection", "keep-alive");
            client.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
            return client;
        }

        private void FillComboBoxes()
        {
            cmbOfficeLanguage.ItemsSource   = officeLanguages;
            cmbOfficeLanguage.SelectedIndex = 0;
        }

        private void SetProgress(bool visible, string phase = "", double value = 0, string detail = "")
        {
            Dispatcher.Invoke(() =>
            {
                pnlProgress.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                txtInstallPhase.Text   = phase;
                progressOffice.Value   = value;
                txtInstallDetail.Text  = detail;
            });
        }

        private void SetPhase(string text) =>
            Dispatcher.Invoke(() => txtInstallPhase.Text = text);

        private void SetDetail(string text) =>
            Dispatcher.Invoke(() => txtInstallDetail.Text = text);

        private static void AddLog(string message) => AppLogger.Write(message);
    }
}
