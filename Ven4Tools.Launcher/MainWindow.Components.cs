using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;
using Ven4Tools.Shared;

namespace Ven4Tools.Launcher
{
    public partial class MainWindow
    {
        private async Task CheckComponentsAutoAsync()
        {
            AddLog("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            AddLog("🔧 Проверка компонентов...");
            bool hasIssues = false;
            bool hasOptionalMissing = false;

            // Постоянные права администратора лаунчеру не нужны: элевация
            // запрашивается точечно при установке компонентов.
            bool isAdmin = IsRunAsAdmin();
            AddLog($"🔍 Права администратора: {(isAdmin ? "✅ есть" : "ℹ️ нет (запросим при необходимости)")}");

            AddLog("🔍 Winget...");
            var wingetInfo = await CheckWingetWithVersionAsync();
            if (wingetInfo.IsInstalled)
            {
                AddLog($"   ✅ Winget {wingetInfo.Version}");
                if (wingetInfo.IsOutdated) { AddLog("   ⚠️ Доступна новая версия winget"); hasIssues = true; }
            }
            else
            {
                AddLog("   ❌ Winget не установлен!");
                hasIssues = true;
            }

            // Chocolatey — опциональный дополнительный источник установки:
            // его отсутствие не считается проблемой и ничего не блокирует
            AddLog("🔍 Chocolatey (опционально)...");
            var chocoInfo = await CheckChocoInstalledAsync();
            if (chocoInfo.IsInstalled)
                AddLog($"   ✅ Chocolatey {chocoInfo.Version}");
            else
            {
                AddLog("   ⚠️ Chocolatey не установлен — по желанию можно установить как дополнительный источник");
                hasOptionalMissing = true;
            }

            AddLog("🔍 WebView2 Runtime...");
            if (IsWebView2Installed())
                AddLog("   ✅ WebView2 Runtime установлен");
            else
            {
                AddLog("   ❌ WebView2 Runtime не установлен");
                hasIssues = true;
            }

            AddLog("🔍 Visual C++ Redistributable 2015-2022 x64...");
            if (IsVcRedistInstalled())
                AddLog("   ✅ Visual C++ Redistributable установлен");
            else
            {
                AddLog("   ❌ Visual C++ Redistributable 2015-2022 x64 не установлен");
                hasIssues = true;
            }

            AddLog("🔍 Версия Windows...");
            if (CheckWindowsVersionOk())
                AddLog($"   ✅ Windows {Environment.OSVersion.Version.Major} Build {Environment.OSVersion.Version.Build}");
            else
            {
                AddLog($"   ⚠️ Windows Build {Environment.OSVersion.Version.Build} ниже минимального (17763)");
                hasIssues = true;
            }

            AddLog("🔍 Свободное место на диске...");
            var (diskOk, freeGB) = CheckDiskSpaceOnDrive(
                _clientPath.Length > 0 ? _clientPath : AppDomain.CurrentDomain.BaseDirectory);
            if (diskOk)
                AddLog(freeGB >= 0 ? $"   ✅ Свободно ≈{freeGB} ГБ" : "   ✅ Место на диске достаточно");
            else
                AddLog($"   ⚠️ Мало свободного места: ≈{freeGB} ГБ (рекомендуется минимум 2 ГБ)");

            AddLog("🔍 Обновления лаунчера...");
            // CDN version.json — основной источник обнаружения версии лаунчера, GitHub —
            // резерв (та же CDN-first логика, что у ручной/фоновой проверки). Раньше здесь
            // была GitHub-only проверка — при блокировке GitHub по SNI обновление не
            // обнаруживалось бы вовсе (структурно идентичная, но не исправленная дыра).
            var launcherUpdateSvc = new LauncherUpdateService(AddLog, _downloadSource);
            var updateInfo = await launcherUpdateSvc.CheckForUpdateAsync();
            if (updateInfo?.HasUpdate == true)
            {
                AddLog($"   📢 Доступно обновление лаунчера {updateInfo.LatestVersion}");
                Dispatcher.Invoke(() => btnInstallUpdate.Visibility = Visibility.Visible);
                hasIssues = true;
            }
            else
            {
                AddLog("   ✅ Лаунчер актуален");
                Dispatcher.Invoke(() => btnInstallUpdate.Visibility = Visibility.Collapsed);
            }

            AddLog("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            if (hasIssues)
            {
                AddLog("⚠️ Найдены проблемы. Нажмите «Установить компоненты».");
                Dispatcher.Invoke(() =>
                {
                    // Кнопка переиспользуется — возвращаем обязательный текст на случай,
                    // если ранее показывался опциональный вариант.
                    btnInstallMissing.Content    = "Установить компоненты";
                    btnInstallMissing.Visibility = Visibility.Visible;
                });
            }
            else if (hasOptionalMissing)
            {
                AddLog("✅ Все обязательные компоненты в порядке.");
                AddLog("ℹ️ Доступен опциональный источник (Chocolatey) — при желании нажмите «Установить Chocolatey».");
                Dispatcher.Invoke(() =>
                {
                    // Не хватает только опционального Chocolatey — кнопка не должна
                    // выглядеть так же призывно, как при реальных проблемах (L2).
                    btnInstallMissing.Content    = "Установить Chocolatey (опционально)";
                    btnInstallMissing.Visibility = Visibility.Visible;
                });
            }
            else
            {
                AddLog("✅ Все компоненты в порядке.");
                Dispatcher.Invoke(() => btnInstallMissing.Visibility = Visibility.Collapsed);
            }
        }

        private async void BtnInstallMissing_Click(object sender, RoutedEventArgs e)
        {
            if (_isUiTestMode)
            {
                AddLog("UI test: установка недостающих компонентов");
                return;
            }

            btnInstallMissing.Visibility = Visibility.Collapsed;
            await CheckComponentsInteractiveAsync();
        }

        private async Task CheckComponentsInteractiveAsync()
        {
            AddLog("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            AddLog("🔧 Устранение проблем...");

            // Перезапуск с правами администратора предлагаем только когда они
            // действительно нужны — для установки WebView2 или VC++ Redistributable.
            // Иначе элевация запрашивается точечно через UAC при запуске установщиков.
            bool isAdmin = IsRunAsAdmin();
            bool needsAdminComponents = !IsWebView2Installed() || !IsVcRedistInstalled();
            if (!isAdmin && needsAdminComponents)
            {
                var restartResult = System.Windows.MessageBox.Show(
                    "Для установки системных компонентов (WebView2, Visual C++ Redistributable)\n" +
                    "потребуются права администратора.\n\n" +
                    "Можно перезапустить лаунчер с правами администратора,\n" +
                    "либо продолжить — тогда запрос UAC появится при запуске установщиков.\n\n" +
                    "Перезапустить с правами администратора сейчас?",
                    "Права администратора",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (restartResult == MessageBoxResult.Yes) { RestartAsAdmin(); return; }
            }

            var wingetInfo = await CheckWingetWithVersionAsync();
            if (!wingetInfo.IsInstalled)
            {
                var installResult = System.Windows.MessageBox.Show(
                    "Winget (Windows Package Manager) не установлен!\n\n" +
                    "Winget необходим для установки большинства приложений.\n\n" +
                    "Установить winget сейчас?",
                    "Требуется winget", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (installResult == MessageBoxResult.Yes)
                {
                    await InstallWingetAsync();
                    wingetInfo = await CheckWingetWithVersionAsync();
                    AddLog(wingetInfo.IsInstalled
                        ? $"   ✅ Winget {wingetInfo.Version}"
                        : "   ⚠️ Winget всё ещё не найден. Возможно, требуется перезагрузка.");
                }
            }
            else if (wingetInfo.IsOutdated)
            {
                var updateResult = System.Windows.MessageBox.Show(
                    $"Ваша версия winget ({wingetInfo.Version}) устарела.\n\nОбновить winget сейчас?",
                    "Обновление winget", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (updateResult == MessageBoxResult.Yes)
                {
                    await InstallWingetAsync();
                    wingetInfo = await CheckWingetWithVersionAsync();
                    AddLog(wingetInfo.IsInstalled
                        ? $"   ✅ Winget {wingetInfo.Version}"
                        : "   ⚠️ Winget всё ещё не обновлён. Возможно, требуется перезагрузка.");
                }
            }

            if (!IsWebView2Installed())
            {
                var r = System.Windows.MessageBox.Show(
                    "WebView2 Runtime не установлен!\n\nУстановить сейчас?",
                    "Требуется WebView2 Runtime", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r == MessageBoxResult.Yes)
                {
                    await InstallWebView2Async();
                    AddLog(IsWebView2Installed()
                        ? "   ✅ WebView2 установлен"
                        : "   ⚠️ WebView2 не обнаружен после установки. Возможно, требуется перезагрузка.");
                }
            }

            if (!IsVcRedistInstalled())
            {
                var r = System.Windows.MessageBox.Show(
                    "Visual C++ Redistributable 2015-2022 x64 не установлен!\n\nУстановить сейчас?",
                    "Требуется Visual C++ Redistributable", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r == MessageBoxResult.Yes)
                {
                    await InstallVcRedistAsync();
                    AddLog(IsVcRedistInstalled()
                        ? "   ✅ Visual C++ Redistributable установлен"
                        : "   ⚠️ VC++ не обнаружен после установки. Возможно, требуется перезагрузка.");
                }
            }

            // Опциональные менеджеры пакетов — предлагаем, но не настаиваем:
            // отказ ничем не грозит, клиент работает и без них
            await OfferOptionalPackageManagersAsync();

            if (!CheckWindowsVersionOk())
            {
                System.Windows.MessageBox.Show(
                    $"Ваша версия Windows (Build {Environment.OSVersion.Version.Build}) " +
                    "ниже минимально поддерживаемой (Windows 10 Build 17763).\n\n" +
                    "Некоторые функции могут работать некорректно.\nРекомендуется обновить Windows.",
                    "Устаревшая версия Windows", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            if (btnInstallUpdate.Visibility == Visibility.Visible)
            {
                var updateResult = System.Windows.MessageBox.Show(
                    "Доступно обновление лаунчера. Установить сейчас?",
                    "Обновление лаунчера", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (updateResult == MessageBoxResult.Yes)
                    await InstallUpdateCoreAsync();
            }

            await CheckComponentsAutoAsync();
        }

        private async Task<(bool IsInstalled, string? Version, bool IsOutdated)> CheckWingetWithVersionAsync()
        {
            try
            {
                var wingetPath = Services.TrustedExecutablePaths.ResolveWinget();
                if (wingetPath == null) return (false, null, false);

                var psi = new ProcessStartInfo
                {
                    FileName               = wingetPath,
                    Arguments              = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };
                using var process = Process.Start(psi);
                if (process == null) return (false, null, false);

                var stderrTask = process.StandardError.ReadToEndAsync();
                var stdoutTask = process.StandardOutput.ReadToEndAsync();

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await process.WaitForExitAsync(timeoutCts.Token); }
                catch (OperationCanceledException)
                {
                    try { process.Kill(); } catch { }
                    return (false, null, false);
                }

                string output = await stdoutTask;
                await stderrTask;

                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                    return (false, null, false);

                string version = output.Trim().TrimStart('v');

                using var gitHubService   = new GitHubService();
                string? latestVersion     = await gitHubService.GetLatestWingetVersionAsync();
                bool isOutdated           = false;
                if (latestVersion != null && Version.TryParse(version, out var current) && Version.TryParse(latestVersion, out var latest))
                    isOutdated = current < latest;

                return (true, version, isOutdated);
            }
            catch { return (false, null, false); }
        }

        private void RestartAsAdmin()
        {
            var exeName = Process.GetCurrentProcess().MainModule?.FileName;
            if (exeName == null) return;

            // Освобождаем мьютекс единственного экземпляра ДО запуска повышенной
            // копии — иначе она может стартовать, пока текущий процесс ещё держит
            // мьютекс (ожидание подтверждения UAC), и выйти как "уже запущен".
            App.ReleaseSingleInstanceMutex();
            var psi = new ProcessStartInfo { FileName = exeName, UseShellExecute = true, Verb = "runas" };
            try
            {
                Process.Start(psi);
            }
            catch
            {
                // Пользователь отклонил UAC (или иная ошибка запуска) — повышенная
                // копия не стартовала. Продолжаем работать в текущем окне, а не
                // закрываемся: без мьютекса лаунчер перестал бы быть единственным
                // экземпляром, поэтому его нужно восстановить.
                App.ReacquireSingleInstanceMutex();
                return;
            }

            _updateService?.Dispose();
            _notifyIcon?.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        // interactive = false — автоматический (marker-driven) вызов из setup:
        // не показываем модальные диалоги и не предлагаем перезагрузку, чтобы
        // ничего не всплывало из скрытого/фонового окна (только запись в лог).
        private async Task InstallWingetAsync(bool interactive = true)
        {
            AddLog("📦 Получение информации о winget с GitHub...");

            string uniq       = Guid.NewGuid().ToString("N");
            string tempMsix   = Path.Combine(Path.GetTempPath(), $"ven4_{uniq}_winget_setup.msixbundle");
            string tempVcLibs = Path.Combine(Path.GetTempPath(), $"ven4_{uniq}_VCLibs.appx");
            string tempUiXaml = Path.Combine(Path.GetTempPath(), $"ven4_{uniq}_UIXaml.appx");

            Dispatcher.Invoke(() =>
            {
                progressDownload.Value    = 0;
                txtDownloadStatus.Text    = "Подготовка...";
                // L4: пользователь мог отменять только скачивание клиента — установка
                // компонентов (winget/WebView2/VC++) не давала прервать зависшую
                // загрузку/установку. Переиспользуем ту же кнопку и CTS-поле.
                btnCancelDownload.Visibility = interactive ? Visibility.Visible : Visibility.Collapsed;
                btnLaunchApp.IsEnabled    = false;
            });

            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                _downloadCts = interactive
                    ? CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token)
                    : null;
                var ct = interactive ? _downloadCts!.Token : timeoutCts.Token;

                string? msixUrl = await ResolveWingetMsixUrlAsync(ct);
                if (msixUrl == null) return;

                await DownloadWingetPackagesAsync(msixUrl, tempVcLibs, tempUiXaml, tempMsix, ct);

                if (!await RunWingetInstallScriptAsync(tempVcLibs, tempUiXaml, tempMsix))
                    return;

                var result = await CheckWingetWithVersionAsync();
                if (result.IsInstalled)
                {
                    AddLog($"✅ Winget {result.Version} успешно установлен");
                    Dispatcher.Invoke(() => txtDownloadStatus.Text = "Winget установлен");
                }
                else
                {
                    AddLog("⚠️ Winget не найден после установки. Возможно, требуется перезагрузка.");
                    Dispatcher.Invoke(() => txtDownloadStatus.Text = "Требуется перезагрузка");

                    if (interactive)
                    {
                        var reboot = System.Windows.MessageBox.Show(
                            "Winget не обнаружен после установки.\n\nПерезагрузить компьютер сейчас?",
                            "Требуется перезагрузка", MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (reboot == MessageBoxResult.Yes)
                            Process.Start(new ProcessStartInfo(Services.TrustedExecutablePaths.ShutdownExe, "/r /t 10") { UseShellExecute = true });
                    }
                    else
                    {
                        // Автоматический вызов из setup: не показываем диалог и не
                        // предлагаем перезагрузку — только сообщаем в лог.
                        AddLog("ℹ️ Winget установлен из setup; для применения может потребоваться перезагрузка — выполните её вручную при необходимости.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                AddLog("⏹ Установка winget отменена");
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "Отменено");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка установки winget: {ex.Message}");
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "Ошибка");
            }
            finally
            {
                try { if (File.Exists(tempMsix))   File.Delete(tempMsix);   } catch { }
                try { if (File.Exists(tempVcLibs)) File.Delete(tempVcLibs); } catch { }
                try { if (File.Exists(tempUiXaml)) File.Delete(tempUiXaml); } catch { }
                _downloadCts?.Dispose();
                _downloadCts = null;
                Dispatcher.Invoke(() =>
                {
                    progressDownload.Value = 0;
                    btnLaunchApp.IsEnabled = true;
                    btnCancelDownload.Visibility = Visibility.Collapsed;
                    btnCancelDownload.IsEnabled = true;
                });
            }
        }

        // Поиск URL основного msixbundle winget (DesktopAppInstaller) в последнем
        // релизе microsoft/winget-cli с проверкой доверенности хоста. null —
        // ассет не найден или хост недоверенный (сообщение уже записано в лог).
        private async Task<string?> ResolveWingetMsixUrlAsync(CancellationToken ct)
        {
            var json    = await _httpClient.GetStringAsync("https://api.github.com/repos/microsoft/winget-cli/releases/latest", ct);
            var release = Newtonsoft.Json.Linq.JObject.Parse(json);

            string? msixUrl = release["assets"]?
                .FirstOrDefault(a => a["name"]?.ToString().EndsWith(".msixbundle") == true &&
                                     a["name"]?.ToString().Contains("DesktopAppInstaller") == true)?
                ["browser_download_url"]?.ToString();

            if (msixUrl == null) { AddLog("❌ Не удалось найти файл установки winget в последнем релизе"); return null; }

            // Защита от подмены: качаем только с доверенных доменов по HTTPS
            if (!DownloadValidator.IsAllowedDownloadHost(msixUrl))
            {
                AddLog($"⛔ Недоверенный URL загрузки winget — скачивание отменено: {msixUrl}");
                return null;
            }
            return msixUrl;
        }

        // Скачивание трёх пакетов winget: зависимости (VCLibs + UI.Xaml) параллельно
        // и без индивидуального прогресса (иначе две загрузки перебивали бы полосу
        // друг у друга), затем основной msixbundle — с прогрессом.
        private async Task DownloadWingetPackagesAsync(
            string msixUrl, string tempVcLibs, string tempUiXaml, string tempMsix, CancellationToken ct)
        {
            AddLog("⬇️ Скачивание зависимостей...");
            Dispatcher.Invoke(() => txtDownloadStatus.Text = "Скачивание зависимостей...");

            var vcLibsTask = DownloadTrustedFileAsync(
                "https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx", tempVcLibs, "VCLibs", reportProgress: false, ct);
            var uiXamlTask = DownloadTrustedFileAsync(
                "https://github.com/microsoft/microsoft-ui-xaml/releases/download/v2.8.6/Microsoft.UI.Xaml.2.8.x64.appx",
                tempUiXaml, "UI.Xaml", reportProgress: false, ct);

            await Task.WhenAll(vcLibsTask, uiXamlTask);

            AddLog($"⬇️ Скачивание winget ({msixUrl.Split('/').Last()})...");
            await DownloadTrustedFileAsync(msixUrl, tempMsix, "Winget", reportProgress: true, ct);
        }

        // Проверка подписи Microsoft у всех трёх пакетов и их установка одним
        // PowerShell-скриптом (Add-AppxPackage). Возвращает false, если хоть один
        // пакет не подписан Microsoft (установка отменена). FileShare.Read держим
        // открытым от проверки подписи до завершения Add-AppxPackage — запрещает
        // подмену файла другим процессом того же пользователя в этом окне (TOCTOU).
        private async Task<bool> RunWingetInstallScriptAsync(string tempVcLibs, string tempUiXaml, string tempMsix)
        {
            using var vcLibsHandle = new FileStream(tempVcLibs, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var uiXamlHandle = new FileStream(tempUiXaml, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var msixHandle   = new FileStream(tempMsix,   FileMode.Open, FileAccess.Read, FileShare.Read);

            foreach (var (path, label) in new[] { (tempVcLibs, "VCLibs"), (tempUiXaml, "UI.Xaml"), (tempMsix, "winget") })
            {
                if (!AuthenticodeVerifier.IsSignedByMicrosoft(path, out string sigError))
                {
                    AddLog($"⛔ Подлинность пакета {label} не подтверждена ({sigError}) — установка отменена");
                    Dispatcher.Invoke(() => txtDownloadStatus.Text = "Подлинность не подтверждена");
                    return false;
                }
            }
            AddLog("✅ Подпись Microsoft подтверждена для всех пакетов");

            AddLog("📦 Установка winget...");
            Dispatcher.Invoke(() => txtDownloadStatus.Text = "Установка...");

            string tempScript = Path.Combine(Path.GetTempPath(), $"winget_install_{Guid.NewGuid():N}.ps1");
            try
            {
                // Одинарные кавычки в PowerShell отключают подстановку $-переменных в путях
                File.WriteAllText(tempScript,
                    $"$ErrorActionPreference = 'Stop'\r\n" +
                    $"try {{ Add-AppxPackage -Path '{tempVcLibs.Replace("'", "''")}' }} catch {{}}\r\n" +
                    $"try {{ Add-AppxPackage -Path '{tempUiXaml.Replace("'", "''")}' }} catch {{}}\r\n" +
                    $"Add-AppxPackage -Path '{tempMsix.Replace("'", "''")}' -ForceApplicationShutdown\r\n",
                    Encoding.UTF8);

                // Держим temp-скрипт открытым с FileShare.Read НЕПРЕРЫВНО от записи до
                // завершения PowerShell — иначе между закрытием файла записи и открытием
                // PowerShell остаётся окно для подмены содержимого другим процессом того
                // же пользователя (TOCTOU). Зеркалирует защиту InstallationService/пакетов
                // VCLibs-UI.Xaml-msix выше в этом же методе.
                using var scriptGuard = new FileStream(tempScript, FileMode.Open, FileAccess.Read, FileShare.Read);

                var psi = new ProcessStartInfo
                {
                    FileName               = Services.TrustedExecutablePaths.PowerShellExe,
                    Arguments              = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\"",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                    string stderr  = await proc.StandardError.ReadToEndAsync();
                    await proc.WaitForExitAsync();
                    await stdoutTask;
                    if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                        AddLog($"⚠️ PowerShell: {stderr.Trim()}");
                }
            }
            finally
            {
                try { File.Delete(tempScript); } catch { }
            }
            return true;
        }

        // Единая загрузка файла с доверенного хоста: потоковое скачивание с проверкой
        // хоста (в т.ч. после редиректов) через FallbackDownloader — одиночный URL
        // без резервного зеркала. reportProgress включает обновление полосы прогресса
        // (для параллельных загрузок его отключаем, чтобы они не перебивали значение
        // друг у друга). Ошибка загрузки/недоверенный хост пробрасываются исключением —
        // вызывающий код (InstallWingetAsync/DownloadVerifyAndRunElevatedAsync) сам
        // сообщает пользователю, поведение остаётся fail-closed.
        private async Task DownloadTrustedFileAsync(
            string url, string destPath, string label, bool reportProgress, CancellationToken ct)
        {
            Action<long, long?>? progress = null;
            if (reportProgress)
                progress = (received, total) =>
                {
                    if (total is > 0)
                    {
                        int pct = (int)((double)received / total.Value * 100);
                        Dispatcher.Invoke(() => { progressDownload.Value = pct; txtDownloadStatus.Text = $"{label}: {pct}%"; });
                    }
                };

            // Одиночный источник (компоненты Microsoft: winget/VCLibs/UI.Xaml/WebView2/VC++)
            // — оборачиваем в список из одного кандидата с обычным клиентом. IP-pinning и
            // хостинг-зеркало этому потоку не нужны (URL не с cdn.ven4tools.ru).
            var downloader = new FallbackDownloader();
            var candidates = new[] { new DownloadCandidate(url, _httpClient, "Источник") };
            // Защитный хендл закрывается сразу — эти файлы (VCLibs/UI.Xaml/msix) получают
            // собственную непрерывную защиту FileShare.Read в RunWingetInstallScriptAsync
            // (открывается заново перед проверкой Authenticode-подписи).
            using var _ = await downloader.DownloadAsync(candidates, destPath, ct, expectedSha256: null, progress: progress);
        }

        // Единый сценарий для установщиков-одиночек Microsoft (WebView2, VC++):
        // потоковое скачивание с доверенного хоста → проверка подписи Microsoft под
        // удерживаемым FileShare.Read-хендлом (защита от TOCTOU) → запуск с точечной
        // элевацией через UAC при отсутствии прав администратора. Winget использует
        // те же строительные блоки (DownloadTrustedFileAsync), но ставится отдельным
        // multi-package PowerShell-скриптом, поэтому идёт своим путём.
        private async Task DownloadVerifyAndRunElevatedAsync(
            string url, string fileName, string args, string label, CancellationToken ct)
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"ven4_{Guid.NewGuid():N}_{fileName}");
            AddLog($"⬇️ Скачивание {label}...");
            // L4: раньше отменить зависшую загрузку/установку WebView2/VC++ было нечем —
            // кнопка «Отмена» показывалась только для скачивания клиента. Переиспользуем
            // ту же кнопку и CTS-поле, связав его с переданным таймаут-токеном.
            _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ct = _downloadCts.Token;
            Dispatcher.Invoke(() =>
            {
                progressDownload.Value = 0;
                txtDownloadStatus.Text = $"{label}: скачивание...";
                btnLaunchApp.IsEnabled = false;
                btnCancelDownload.Visibility = Visibility.Visible;
            });
            try
            {
                await DownloadTrustedFileAsync(url, tempFile, label, reportProgress: true, ct);

                // Скачано с доверенного хоста Microsoft по HTTPS, но перед запуском с
                // повышением прав дополнительно подтверждаем подпись Microsoft
                // (допускает штатное обновление содержимого по URL). FileShare.Read
                // держим открытым от проверки до запуска: запрещает подмену файла
                // другим процессом того же пользователя в этом окне.
                using (new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (!AuthenticodeVerifier.IsSignedByMicrosoft(tempFile, out string sigError))
                    {
                        AddLog($"⛔ Подлинность установщика {label} не подтверждена ({sigError}) — установка отменена");
                        Dispatcher.Invoke(() => txtDownloadStatus.Text = "Подлинность не подтверждена");
                        return;
                    }
                    AddLog($"✅ Подпись Microsoft подтверждена ({label})");

                    AddLog($"📦 Установка {label}...");
                    Dispatcher.Invoke(() => txtDownloadStatus.Text = $"{label}: установка...");
                    // Без прав администратора — точечная элевация через UAC (Verb = runas)
                    bool needElevation = !IsRunAsAdmin();
                    var psi = new ProcessStartInfo
                    {
                        FileName = tempFile, Arguments = args,
                        UseShellExecute = needElevation, CreateNoWindow = !needElevation
                    };
                    if (needElevation) psi.Verb = "runas";
                    using var proc = Process.Start(psi);
                    if (proc != null) await proc.WaitForExitAsync();
                }
                // Завершение процесса установщика ≠ успех: exit code Microsoft-установщиков
                // ненадёжен, а WebView2/VC++ могут «встать» только после перезагрузки.
                // Единственный источник правды об успехе — перепроверка через
                // IsWebView2Installed()/IsVcRedistInstalled() у вызывающего кода
                // (CheckComponentsInteractiveAsync), поэтому здесь успех НЕ логируем,
                // чтобы не было противоречия «✅ установлен» + «⚠️ не обнаружен».
                Dispatcher.Invoke(() => { progressDownload.Value = 100; txtDownloadStatus.Text = $"{label}: установка завершена"; });
            }
            catch (OperationCanceledException)
            {
                AddLog($"⏹ Установка {label} отменена");
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "Отменено");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка установки {label}: {ex.Message}");
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "Ошибка");
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                _downloadCts?.Dispose();
                _downloadCts = null;
                Dispatcher.Invoke(() =>
                {
                    progressDownload.Value = 0;
                    btnLaunchApp.IsEnabled = true;
                    btnCancelDownload.Visibility = Visibility.Collapsed;
                    btnCancelDownload.IsEnabled = true;
                });
            }
        }

        private bool IsRunAsAdmin()
        {
            var identity  = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        // Запущен ли клиент Ven4Tools из текущей папки установки.
        private bool IsClientRunning()
        {
            var proc = FindRunningClientProcess();
            proc?.Dispose();
            return proc != null;
        }

        // Находит процесс запущенного клиента из текущей папки установки и возвращает
        // его НЕ освобождённым — вызывающий (TryCloseRunningClientAsync) сам вызывает
        // Dispose(). Остальные (непарные) найденные процессы освобождаются здесь же.
        // Если MainModule недоступен — считаем процесс совпадением: безопаснее показать
        // предупреждение лишний раз, чем оставить папку клиента в битом состоянии.
        private Process? FindRunningClientProcess()
        {
            string clientExe = Path.Combine(_clientPath, LauncherPaths.ClientExeName);
            Process[] processes;
            try { processes = Process.GetProcessesByName("Ven4Tools"); }
            catch { return null; }

            foreach (var proc in processes)
            {
                bool isMatch;
                try
                {
                    string? exePath = proc.MainModule?.FileName;
                    isMatch = string.IsNullOrEmpty(exePath) ||
                              string.Equals(exePath, clientExe, StringComparison.OrdinalIgnoreCase);
                }
                catch { isMatch = true; }

                if (isMatch)
                {
                    foreach (var other in processes) if (other != proc) other.Dispose();
                    return proc;
                }
                proc.Dispose();
            }
            return null;
        }

        // Просит запущенный elevated-клиент закрыться через именованный pipe и ждёт
        // до timeoutMs, пока процесс завершится. WM_CLOSE здесь неприменим: launcher
        // работает asInvoker, и Windows UIPI блокирует его сообщения elevated-окну.
        // Клиент сам решает, закрываться ли (см. Window_Closing_Extended в
        // Ven4Tools/MainWindow.xaml.cs — предупреждение при активной установке,
        // либо сворачивание в трей вместо закрытия при включённой у клиента
        // соответствующей настройке — тогда процесс не завершится, и этот метод
        // вернёт false по таймауту; форсированный Process.Kill() не используется).
        private async Task<bool> TryCloseRunningClientAsync(int timeoutMs = 10000)
        {
            var proc = FindRunningClientProcess();
            if (proc == null) return true;

            proc.Dispose();

            if (!await ClientControlChannel.RequestShutdownAsync())
            {
                AddLog("⚠️ Клиент не принял запрос на штатное закрытие");
                return false;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!IsClientRunning()) return true;
                await Task.Delay(500);
            }
            return false;
        }

        private static bool IsWebView2Installed()
        {
            string[] machinePaths = {
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
            };
            foreach (var p in machinePaths)
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(p);
                if (k?.GetValue("pv") is string v && !string.IsNullOrEmpty(v) && v != "0.0.0.0")
                    return true;
            }
            using var uk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
            if (uk?.GetValue("pv") is string uv && !string.IsNullOrEmpty(uv) && uv != "0.0.0.0")
                return true;
            return false;
        }

        private static bool IsVcRedistInstalled()
        {
            string[] keyPaths = {
                @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64",
                @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X64"
            };
            foreach (var p in keyPaths)
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(p);
                if (k?.GetValue("Installed") is int v && v == 1)
                    return true;
            }
            return false;
        }

        private static bool CheckWindowsVersionOk()
        {
            var v = Environment.OSVersion.Version;
            if (v.Major < 10) return false;
            if (v.Major == 10 && v.Build < 17763) return false;
            return true;
        }

        private static (bool Ok, long FreeGB) CheckDiskSpaceOnDrive(string path)
        {
            try
            {
                string root  = Path.GetPathRoot(path) ?? "C:\\";
                var drive    = new DriveInfo(root);
                long freeGB  = drive.AvailableFreeSpace / (1024L * 1024 * 1024);
                return (freeGB >= 2, freeGB);
            }
            catch { return (true, -1); }
        }

        private async Task InstallWebView2Async()
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await DownloadVerifyAndRunElevatedAsync(
                "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                "MicrosoftEdgeWebview2Setup.exe",
                "/silent /install",
                "WebView2",
                timeoutCts.Token);
        }

        private async Task InstallVcRedistAsync()
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await DownloadVerifyAndRunElevatedAsync(
                "https://aka.ms/vs/17/release/vc_redist.x64.exe",
                "vc_redist.x64.exe",
                "/install /quiet /norestart",
                "VC++",
                timeoutCts.Token);
        }

        private static CrashReport? ReadCrashReport()
        {
            try
            {
                string path = LauncherPaths.CrashReportPath;
                if (!System.IO.File.Exists(path)) return null;
                return Newtonsoft.Json.JsonConvert.DeserializeObject<CrashReport>(
                    System.IO.File.ReadAllText(path));
            }
            catch { return null; }
        }

        private static System.Collections.Generic.List<InstallFailure> ReadInstallFailures()
        {
            try
            {
                string path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Ven4Tools", "failed_installs.json");
                if (!System.IO.File.Exists(path)) return new();
                var list = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<InstallFailure>>(
                    System.IO.File.ReadAllText(path)) ?? new();
                return list.FindAll(f => !f.Reported);
            }
            catch { return new(); }
        }
    }
}
