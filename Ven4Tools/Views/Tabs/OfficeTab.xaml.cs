using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Newtonsoft.Json;
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

        // Persistent-маркер региона на диске — страховка от hard-kill / отключения питания
        // между SetRegionUS() и RestoreRegion(). Если процесс убит, файл переживёт и регион
        // будет восстановлен при следующем запуске (см. конструктор).
        private static readonly string _regionBackupPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "region_backup.json");

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

            // Восстановление региона после аварийного завершения (hard-kill / отключение питания
            // во время установки Office, когда finally в BtnInstallOffice_Click не успел отработать).
            RecoverRegionFromBackup();

            FillComboBoxes();

            btnDownloadOffice.Click += BtnDownloadOffice_Click;
            btnInstallOffice.Click  += BtnInstallOffice_Click;
            btnCancelOffice.Click   += (_, _) =>
            {
                _cancellationTokenSource?.Cancel();
                btnCancelOffice.IsEnabled = false;
                AppLogger.Write("⏹️ Запрос отмены...");
            };
            btnGoActivation.Click += (_, _) => GoToActivation?.Invoke();

            // M2: смена версии/языка после скачивания должна сбрасывать уже скачанный
            // установщик — иначе «Установить» тихо поставит старую версию/язык, тогда как
            // лог/UI показывают новое выбранное значение. Подписки — после FillComboBoxes,
            // чтобы начальный SelectedIndex=0 не срабатывал как «смена».
            rdbO365.Checked  += OnOfficeSelectionChanged;
            rdbO2024.Checked += OnOfficeSelectionChanged;
            rdbO2021.Checked += OnOfficeSelectionChanged;
            rdbO2019.Checked += OnOfficeSelectionChanged;
            rdbO2016.Checked += OnOfficeSelectionChanged;
            cmbOfficeLanguage.SelectionChanged += OnOfficeSelectionChanged;

            pnlActivationHint.Visibility = Visibility.Visible;
            UpdateRegionDisplay();
        }

        // M2: при смене версии/языка удаляем ранее скачанный установщик и блокируем
        // «Установить», чтобы нельзя было поставить не то, что показано в UI.
        private void OnOfficeSelectionChanged(object sender, RoutedEventArgs e)
        {
            if (_downloadedFilePath == null) return;

            try { if (File.Exists(_downloadedFilePath)) File.Delete(_downloadedFilePath); } catch { }
            _downloadedFilePath = null;
            btnInstallOffice.IsEnabled = false;
            AppLogger.Write("ℹ️ Версия/язык изменены — скачайте установщик заново");
            SetProgress(true, "ℹ️ Версия/язык изменены — скачайте установщик заново", 0, "");
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

            // Persistent-маркер: сохраняем исходный регион на диск ДО SetRegionUS(),
            // чтобы при аварийном завершении его можно было восстановить при следующем запуске.
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_regionBackupPath)!);
                var backup = new RegionBackup
                {
                    OfficeCC   = _originalOfficeCC,
                    GeoName    = _originalGeoName,
                    GeoNation  = _originalGeoNation
                };
                File.WriteAllText(_regionBackupPath, JsonConvert.SerializeObject(backup));
            }
            catch (Exception ex) { AppLogger.Write($"⚠️ Сохранение маркера региона: {ex.Message}"); }
        }

        // Восстановление региона из persistent-маркера при старте (после hard-kill).
        private void RecoverRegionFromBackup()
        {
            try
            {
                if (!File.Exists(_regionBackupPath)) return;

                var backup = JsonConvert.DeserializeObject<RegionBackup>(File.ReadAllText(_regionBackupPath));
                if (backup == null)
                {
                    try { File.Delete(_regionBackupPath); } catch { }
                    return;
                }

                // Office CountryCode — те же ключи, что и в RestoreRegion()
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs", writable: true);
                    if (key != null)
                    {
                        if (backup.OfficeCC != null)
                        {
                            if (IsValidRegionValue(backup.OfficeCC))
                                key.SetValue("CountryCode", backup.OfficeCC, RegistryValueKind.String);
                            else
                                AppLogger.Write($"Невалидное значение региона (OfficeCC): {backup.OfficeCC}");
                        }
                        else
                            key.DeleteValue("CountryCode", throwOnMissingValue: false);
                    }
                }
                catch { /* ключа может не быть — игнорируем */ }

                // Windows GeoID
                try
                {
                    using var geo = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\Geo", writable: true);
                    if (geo != null)
                    {
                        if (backup.GeoName != null)
                        {
                            if (IsValidRegionValue(backup.GeoName))
                                geo.SetValue("Name", backup.GeoName, RegistryValueKind.String);
                            else
                                AppLogger.Write($"Невалидное значение региона (GeoName): {backup.GeoName}");
                        }

                        if (backup.GeoNation != null)
                        {
                            if (IsValidRegionValue(backup.GeoNation))
                                geo.SetValue("Nation", backup.GeoNation, RegistryValueKind.String);
                            else
                                AppLogger.Write($"Невалидное значение региона (GeoNation): {backup.GeoNation}");
                        }
                        else
                            geo.DeleteValue("Nation", throwOnMissingValue: false);
                    }
                }
                catch { /* игнорируем */ }

                try { File.Delete(_regionBackupPath); } catch { }
                AppLogger.Write("🔁 Регион восстановлен после аварийного завершения предыдущей установки Office");
            }
            catch (Exception ex) { AppLogger.Write($"⚠️ Восстановление региона из маркера: {ex.Message}"); }
        }

        // Валидация значений региона из region_backup.json перед записью в реестр.
        // Допускаются только буквы, цифры, пробелы и безопасные разделители (включая
        // формат Office CountryCode вида "std::wstring|US"). Макс. длина — 100 символов.
        private static bool IsValidRegionValue(string value)
        {
            return !string.IsNullOrEmpty(value)
                && value.Length <= 100
                && System.Text.RegularExpressions.Regex.IsMatch(value, @"^[\w\s\-.,:|]+$");
        }

        // Модель persistent-маркера региона (region_backup.json). Поля могут быть null.
        private sealed class RegionBackup
        {
            public string? OfficeCC   { get; set; }
            public string? GeoName    { get; set; }
            public string? GeoNation  { get; set; }
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
            catch (Exception ex) { AppLogger.Write($"⚠️ Office CountryCode: {ex.Message}"); }

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
                    AppLogger.Write("⚠️ Control Panel\\International\\Geo — ключ не найден");
            }
            catch (Exception ex) { AppLogger.Write($"⚠️ Windows GeoID: {ex.Message}"); }

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
            catch (Exception ex) { AppLogger.Write($"⚠️ Восстановление Office CC: {ex.Message}"); }

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
            catch (Exception ex) { AppLogger.Write($"⚠️ Восстановление Windows GeoID: {ex.Message}"); }

            // Регистр восстановлен — удаляем persistent-маркер, он больше не нужен.
            try { if (File.Exists(_regionBackupPath)) File.Delete(_regionBackupPath); } catch { }

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
            btnCancelOffice.Visibility  = Visibility.Visible;

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            SetProgress(true, "⏳ Подготовка...", 0, "");
            AppLogger.Write($"\n📥 Скачивание {displayName} ({lang})...");

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
                AppLogger.Write($"✅ Скачано: {fi.Length / 1_048_576.0:F1} МБ");
                SetProgress(true, "✅ Скачано! Нажмите «Установить»", 100,
                    $"{fi.Length / 1_048_576.0:F1} МБ");

                _downloadedFilePath = tempFile;
                btnInstallOffice.IsEnabled = true;
            }
            catch (OperationCanceledException)
            {
                AppLogger.Write("⏹️ Скачивание отменено");
                SetProgress(true, "⏹️ Отменено", 0, "");
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка скачивания: {ex.Message}");
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
                AppLogger.Write("⚠️ Файл установщика не найден — скачайте снова.");
                btnInstallOffice.IsEnabled = false;
                return;
            }
            string installerPath = _downloadedFilePath;

            var (displayName, _) = GetSelectedVersion();

            btnInstallOffice.IsEnabled  = false;
            btnDownloadOffice.IsEnabled = false;
            btnCancelOffice.IsEnabled   = true;
            btnCancelOffice.Visibility  = Visibility.Visible;

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;
            bool regionChanged = false;

            SetProgress(true, "⏳ Подготовка установки...", 0, "");
            AppLogger.Write($"\n🚀 Установка {displayName}...");

            try
            {
                SetPhase("🔐 Проверка подлинности установщика...");

                // FileShare.Read держим открытым от проверки подписи до запуска
                // установщика — запрещает подмену файла другим процессом того же
                // пользователя в этом окне (TOCTOU), как в MainWindow.Components.cs
                // (InstallWebView2Async/InstallVcRedistAsync лаунчера). Хендл
                // закрывается явно (не using var на весь блок), чтобы не держать
                // файл заблокированным для удаления в ветке отказа проверки ниже.
                var installerHandle = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);

                if (!VerifyMicrosoftInstallerSignature(installerPath, out string signatureError))
                {
                    installerHandle.Dispose();
                    AppLogger.Write("❌ Не удалось подтвердить подлинность установщика Microsoft — скачайте заново");
                    AppLogger.Write($"   Причина: {signatureError}");
                    TryDeleteDownloadedInstaller();
                    SetProgress(true, "❌ Подлинность не подтверждена", 0, "Скачайте установщик заново.");
                    MessageBox.Show("Не удалось подтвердить подлинность установщика Microsoft — скачайте заново.",
                        "Проверка установщика", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                AppLogger.Write("✅ Подпись установщика Microsoft подтверждена");

                SaveRegion();
                regionChanged = true; // до SetRegionUS — чтобы finally откатил даже при исключении внутри
                SetRegionUS();
                AppLogger.Write("🌎 Регион переключён на US (GeoID: 244, CountryCode: US)");

                SetPhase("🚀 Запуск установщика...");
                var existingPids = GetC2RProcessPids();

                // Последняя точка, где отмена ещё безопасна: если пользователь нажал
                // «Отмена» на этапе проверки подписи — прерываемся ДО запуска установщика
                // (регион восстановит finally). После Process.Start отмена уже недоступна.
                token.ThrowIfCancellationRequested();

                using var bootstrapper = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo
                    {
                        FileName        = installerPath,
                        UseShellExecute = true,
                        Verb            = "runas"
                    });
                // ShellExecuteEx уже открыл/запустил файл к моменту возврата
                // из Process.Start — хендл-защита от подмены больше не нужна.
                installerHandle.Dispose();

                if (bootstrapper != null)
                {
                    // M3: elevated-процесс установщика уже запущен — реальную установку
                    // отменить нельзя (регион будет восстановлен только после её завершения).
                    // Прячем «Отмена», чтобы UI не обещал невозможного.
                    Dispatcher.Invoke(() =>
                    {
                        btnCancelOffice.IsEnabled  = false;
                        btnCancelOffice.Visibility = Visibility.Collapsed;
                    });
                    SetPhase("⚙️ Установка Office запущена — отменить нельзя, дождитесь завершения");

                    await bootstrapper.WaitForExitAsync(token);
                    if (bootstrapper.ExitCode != 0)
                    {
                        AppLogger.Write($"❌ Установщик завершился с кодом {bootstrapper.ExitCode}");
                        AppLogger.Write("   Вероятная причина: CDN Microsoft заблокирован в вашем регионе.");
                        AppLogger.Write("   Попробуйте использовать VPN и повторить установку.");
                        SetProgress(true, $"❌ Сбой установки (код {bootstrapper.ExitCode})", 0,
                            "CDN Microsoft может быть недоступен. Попробуйте VPN.");
                        return;
                    }
                }

                token.ThrowIfCancellationRequested();

                SetPhase("⚙️ Установка Office... не закрывайте приложение");
                AppLogger.Write("⏳ Ожидаем запуск C2R-установщика...");

                using var installProc = await WaitForC2RProcess(existingPids, TimeSpan.FromMinutes(3), token);

                if (installProc == null)
                {
                    AppLogger.Write("⚠️ Процесс установки не обнаружен — возможно Office уже установлен или завершился мгновенно");
                }
                else
                {
                    AppLogger.Write($"🔍 Мониторинг: {installProc.ProcessName} (PID {installProc.Id})");
                    SetProgress(true, "⚙️ Установка Office...", 0, "Идёт установка, пожалуйста подождите...");
                    progressOffice.IsIndeterminate = true;
                    await MonitorInstallation(installProc, token);
                    progressOffice.IsIndeterminate = false;
                }

                token.ThrowIfCancellationRequested();

                RestoreRegion();
                regionChanged = false;
                AppLogger.Write("✅ Установка завершена — регион восстановлен");
                SetProgress(true, "✅ Офис установлен!", 100, "Регион восстановлен");

                if (chkSaveInstaller.IsChecked != true)
                {
                    TryDeleteDownloadedInstaller();
                }
            }
            catch (OperationCanceledException)
            {
                AppLogger.Write("⏹️ Установка отменена");
                SetProgress(true, "⏹️ Отменено", 0, "");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка установки: {ex.Message}");
                SetProgress(true, "❌ Ошибка установки", 0, "");
                MessageBox.Show("Не удалось установить Office. Попробуйте ещё раз или установите вручную.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (regionChanged)
                {
                    RestoreRegion();
                    AppLogger.Write("🔁 Регион восстановлен (аварийный сброс)");
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

        private static bool VerifyMicrosoftInstallerSignature(string filePath, out string error)
        {
            if (!File.Exists(filePath))
            {
                error = "файл не найден";
                return false;
            }

            int trustStatus = NativeMethods.VerifyAuthenticodeSignature(filePath);

            // Проверка отзыва идёт по всей цепочке (см. fdwRevocationChecks ниже), но
            // требует сети. Недоступность CRL/OCSP не считаем провалом — сама подпись
            // остаётся fail-closed, отозванный сертификат вернёт CERT_E_REVOKED и попадёт
            // в общую ветку отказа. Тот же подход, что в AuthenticodeVerifier лаунчера.
            const int CERT_E_REVOCATION_FAILURE = unchecked((int)0x80092012);
            const int CRYPT_E_REVOCATION_OFFLINE = unchecked((int)0x80092013);
            if (trustStatus != 0 &&
                trustStatus != CERT_E_REVOCATION_FAILURE &&
                trustStatus != CRYPT_E_REVOCATION_OFFLINE)
            {
                error = $"проверка Authenticode вернула код 0x{trustStatus:X8}";
                return false;
            }

            try
            {
#pragma warning disable SYSLIB0057
                // Сертификат читается только после WinVerifyTrust, чтобы сверить издателя.
                using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
#pragma warning restore SYSLIB0057
                if (HasExactOrganization(certificate.SubjectName, "Microsoft Corporation"))
                {
                    error = "";
                    return true;
                }

                error = $"неожиданный издатель: {certificate.Subject}";
                return false;
            }
            catch (Exception ex)
            {
                error = $"не удалось прочитать сертификат: {ex.Message}";
                return false;
            }
        }

        // Сравнение по значению поля O= (Organization) через разбор RDN, а не подстрокой
        // Subject целиком — Contains("O=Microsoft Corporation") пропустил бы издателя вида
        // "O=Microsoft Corporation Something", у которого значение O на самом деле другое.
        private static bool HasExactOrganization(X500DistinguishedName name, string expected)
        {
            foreach (string line in name.Format(true).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim().TrimEnd('\r');
                if (trimmed.StartsWith("O=", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(trimmed[2..], expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private void TryDeleteDownloadedInstaller()
        {
            if (_downloadedFilePath == null)
                return;

            try { File.Delete(_downloadedFilePath); } catch { }
            _downloadedFilePath = null;
        }

        private static class NativeMethods
        {
            private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

            // WTD_REVOKE_WHOLECHAIN — проверять отзыв по всей цепочке сертификатов,
            // а не только для листового (WTD_REVOKE_NONE = 0, как было раньше).
            private const uint WTD_REVOKE_WHOLECHAIN = 0x00000001;

            public static int VerifyAuthenticodeSignature(string filePath)
            {
                IntPtr filePathPtr = IntPtr.Zero;
                IntPtr fileInfoPtr = IntPtr.Zero;
                try
                {
                    filePathPtr = Marshal.StringToCoTaskMemUni(filePath);
                    var fileInfo = new WintrustFileInfo
                    {
                        cbStruct      = (uint)Marshal.SizeOf<WintrustFileInfo>(),
                        pcwszFilePath = filePathPtr,
                        hFile         = IntPtr.Zero,
                        pgKnownSubject = IntPtr.Zero
                    };

                    fileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WintrustFileInfo>());
                    Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

                    var trustData = new WintrustData
                    {
                        cbStruct            = (uint)Marshal.SizeOf<WintrustData>(),
                        pPolicyCallbackData = IntPtr.Zero,
                        pSIPClientData      = IntPtr.Zero,
                        dwUIChoice          = 2,
                        fdwRevocationChecks = WTD_REVOKE_WHOLECHAIN,
                        dwUnionChoice       = 1,
                        pFile               = fileInfoPtr,
                        dwStateAction       = 0,
                        hWVTStateData       = IntPtr.Zero,
                        pwszURLReference    = IntPtr.Zero,
                        dwProvFlags         = 0,
                        dwUIContext         = 0,
                        pSignatureSettings  = IntPtr.Zero
                    };

                    return WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, ref trustData);
                }
                finally
                {
                    if (fileInfoPtr != IntPtr.Zero)
                        Marshal.FreeCoTaskMem(fileInfoPtr);
                    if (filePathPtr != IntPtr.Zero)
                        Marshal.FreeCoTaskMem(filePathPtr);
                }
            }

            [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern int WinVerifyTrust(
                IntPtr hwnd,
                [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionId,
                ref WintrustData pWVTData);

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct WintrustFileInfo
            {
                public uint cbStruct;
                public IntPtr pcwszFilePath;
                public IntPtr hFile;
                public IntPtr pgKnownSubject;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct WintrustData
            {
                public uint cbStruct;
                public IntPtr pPolicyCallbackData;
                public IntPtr pSIPClientData;
                public uint dwUIChoice;
                public uint fdwRevocationChecks;
                public uint dwUnionChoice;
                public IntPtr pFile;
                public uint dwStateAction;
                public IntPtr hWVTStateData;
                public IntPtr pwszURLReference;
                public uint dwProvFlags;
                public uint dwUIContext;
                public IntPtr pSignatureSettings;
            }
        }

        // ── Помощники для процессов C2R ───────────────────────────────────────

        private static HashSet<int> GetC2RProcessPids()
        {
            var names = new[] { "officec2rclient", "OfficeClickToRun" };
            var pids  = new HashSet<int>();
            foreach (var name in names)
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                    using (p) pids.Add(p.Id);
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
                    {
                        // Найденный процесс возвращаем (его освобождает вызывающий),
                        // остальные снимки процессов освобождаем сразу.
                        if (!existingPids.Contains(p.Id))
                            return p;
                        p.Dispose();
                    }

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
                AppLogger.Write("⚠️ Таймаут ожидания — продолжаем без подтверждения");
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

    }
}
