using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Ven4Tools.Models;

namespace Ven4Tools.Views.Tabs
{
    public partial class OfficeTab : UserControl
    {
        private static readonly HttpClient _httpClient = CreateHttpClient();
        private CancellationTokenSource? _cancellationTokenSource;

        public event Action<string>? LogMessage;
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

        private string? _originalCountryCode;

        public OfficeTab()
        {
            InitializeComponent();
            FillComboBoxes();
            btnInstallOffice.Click += BtnInstallOffice_Click;
            btnCancelOffice.Click += (_, _) =>
            {
                _cancellationTokenSource?.Cancel();
                btnCancelOffice.IsEnabled = false;
                AddLog("⏹️ Запрос отмены...");
            };
            btnGoActivation.Click += (_, _) => GoToActivation?.Invoke();

            UserSession.Changed += UpdateActivationPanel;
            Unloaded += (_, _) => UserSession.Changed -= UpdateActivationPanel;
            UpdateActivationPanel();
        }

        private void UpdateActivationPanel()
        {
            Dispatcher.Invoke(() =>
                pnlActivationHint.Visibility = UserSession.IsLoggedIn ? Visibility.Visible : Visibility.Collapsed);
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
            cmbOfficeLanguage.ItemsSource = officeLanguages;
            cmbOfficeLanguage.SelectedIndex = 0;
        }

        private (string DisplayName, string ProductId) GetSelectedVersion()
        {
            if (rdbO2024.IsChecked == true) return ("Office 2024 ProPlus",      "ProPlus2024Retail");
            if (rdbO2021.IsChecked == true) return ("Office 2021 Professional",  "Professional2021Retail");
            if (rdbO2019.IsChecked == true) return ("Office 2019 Professional",  "Professional2019Retail");
            if (rdbO2016.IsChecked == true) return ("Office 2016 Professional",  "ProPlusRetail");
            return ("Office 365 ProPlus", "O365ProPlusRetail");
        }

        private void SaveOriginalCountryCode()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs");
                _originalCountryCode = key?.GetValue("CountryCode")?.ToString();
            }
            catch { _originalCountryCode = null; }
        }

        private void SetCountryCode(string countryCode)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs");
                key?.SetValue("CountryCode", $"std::wstring|{countryCode}", RegistryValueKind.String);
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ Ошибка при установке CountryCode: {ex.Message}");
            }
        }

        private void RestoreOriginalCountryCode()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs", true);
                if (key != null)
                {
                    if (_originalCountryCode != null)
                        key.SetValue("CountryCode", _originalCountryCode, RegistryValueKind.String);
                    else
                        key.DeleteValue("CountryCode", false);
                }
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ Ошибка при восстановлении CountryCode: {ex.Message}");
            }
        }

        private async void BtnInstallOffice_Click(object sender, RoutedEventArgs e)
        {
            if (cmbOfficeLanguage.SelectedItem == null)
                return;

            var (displayName, productId) = GetSelectedVersion();
            string lang = cmbOfficeLanguage.SelectedItem.ToString()!;

            btnInstallOffice.IsEnabled = false;
            btnCancelOffice.IsEnabled  = true;
            _cancellationTokenSource   = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            SetProgress(true, "⏳ Подготовка...", 0);
            AddLog($"\n📦 Установка {displayName} ({lang})...");

            string tempFile = Path.Combine(Path.GetTempPath(), $"OfficeSetup_{Guid.NewGuid():N}.exe");
            bool regionChanged = false;

            try
            {
                SaveOriginalCountryCode();
                SetCountryCode("US");
                regionChanged = true;
                AddLog("🌎 CountryCode = US (будет восстановлен после установки)");

                // ── Фаза 1: скачивание ────────────────────────────────────────
                SetPhase("📥 Скачивание установщика...");
                string downloadUrl = string.Format(officeDirectLinks[productId], lang);

                using (var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
                {
                    response.EnsureSuccessStatusCode();
                    using var src  = await response.Content.ReadAsStreamAsync();
                    using var dst  = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);
                    var buf        = new byte[65536];
                    int read;
                    long total     = 0;
                    long? size     = response.Content.Headers.ContentLength;
                    int lastPct    = -1;

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
                                SetProgress(true, $"📥 Скачивание: {pct}%", pct, $"{total / 1048576:F1} / {size.Value / 1048576:F1} МБ");
                            }
                        }
                    }
                }

                if (token.IsCancellationRequested)
                {
                    AddLog("⏹️ Скачивание прервано");
                    return;
                }

                var fi = new FileInfo(tempFile);
                AddLog($"✅ Скачано: {fi.Length / 1048576:F1} МБ");

                // ── Фаза 2: запуск бутстраппера ──────────────────────────────
                SetPhase("🚀 Запуск установщика...");
                AddLog("🚀 Запуск установщика Office...");

                var bootstrapper = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = tempFile,
                    UseShellExecute = true,
                    Verb            = "runas"
                });

                // Запомним PID-ы уже запущенных c2r-процессов, чтобы не перепутать
                var existingPids = GetC2RProcessPids();

                // Ждём, пока бутстраппер передаст управление C2R
                if (bootstrapper != null)
                    await bootstrapper.WaitForExitAsync(token);

                // ── Фаза 3: мониторинг реальной установки ────────────────────
                SetPhase("⚙️ Установка Office... не закрывайте приложение");
                AddLog("⏳ Ожидаем запуск C2R-установщика...");

                var installProc = await WaitForC2RProcess(existingPids, TimeSpan.FromMinutes(3), token);

                if (installProc == null)
                {
                    AddLog("⚠️ Процесс установки не обнаружен — возможно Office уже установлен или установка прошла мгновенно");
                }
                else
                {
                    AddLog($"🔍 Мониторинг: {installProc.ProcessName} (PID {installProc.Id})");
                    SetProgress(true, "⚙️ Установка Office...", 0, "Идёт установка, пожалуйста подождите...");
                    progressOffice.IsIndeterminate = true;

                    await MonitorInstallation(installProc, token);

                    progressOffice.IsIndeterminate = false;
                }

                // ── Готово ────────────────────────────────────────────────────
                AddLog("✅ Установка завершена — восстанавливаем регион");
                RestoreOriginalCountryCode();
                regionChanged = false;

                SetProgress(true, "✅ Офис установлен!", 100, "Регион восстановлен");
                AddLog("✅ Регион восстановлен");

                if (chkSaveInstaller.IsChecked != true)
                    try { File.Delete(tempFile); } catch { }
            }
            catch (OperationCanceledException)
            {
                AddLog("⏹️ Операция отменена");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка: {ex.Message}");
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (regionChanged)
                {
                    RestoreOriginalCountryCode();
                    AddLog("🔁 Регион восстановлен (аварийный сброс)");
                }
                try { if (File.Exists(tempFile) && chkSaveInstaller.IsChecked != true) File.Delete(tempFile); } catch { }
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                Dispatcher.Invoke(() =>
                {
                    btnInstallOffice.IsEnabled = true;
                    btnCancelOffice.IsEnabled  = false;
                });
                await Task.Delay(3000);
                SetProgress(false);
            }
        }

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
                {
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                    {
                        if (!existingPids.Contains(p.Id))
                            return p;
                    }
                }
                await Task.Delay(2000, token);
            }
            return null;
        }

        private async Task MonitorInstallation(System.Diagnostics.Process proc, CancellationToken token)
        {
            var timeout  = TimeSpan.FromMinutes(60);
            var deadline = DateTime.UtcNow + timeout;
            var elapsed  = System.Diagnostics.Stopwatch.StartNew();

            while (!proc.HasExited && DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            {
                await Task.Delay(5000, token);
                SetDetail($"Установка идёт {elapsed.Elapsed:mm\\:ss}...");
            }

            if (!proc.HasExited)
                AddLog("⚠️ Таймаут ожидания — продолжаем без подтверждения");
        }

        private void SetProgress(bool visible, string phase = "", double value = 0, string detail = "")
        {
            Dispatcher.Invoke(() =>
            {
                pnlProgress.Visibility    = visible ? Visibility.Visible : Visibility.Collapsed;
                txtInstallPhase.Text      = phase;
                progressOffice.Value      = value;
                txtInstallDetail.Text     = detail;
            });
        }

        private void SetPhase(string text) =>
            Dispatcher.Invoke(() => txtInstallPhase.Text = text);

        private void SetDetail(string text) =>
            Dispatcher.Invoke(() => txtInstallDetail.Text = text);

        private void AddLog(string message)
        {
            LogMessage?.Invoke(message);
        }
    }
}
