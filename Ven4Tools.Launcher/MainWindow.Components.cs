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
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var currentVersion = ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "0.0.0";
            using var gitHubServiceCheck = new GitHubService();
            var updateInfo = await gitHubServiceCheck.CheckLauncherUpdate(currentVersion);
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
                Dispatcher.Invoke(() => btnInstallMissing.Visibility = Visibility.Visible);
            }
            else if (hasOptionalMissing)
            {
                AddLog("✅ Все обязательные компоненты в порядке.");
                AddLog("ℹ️ Доступен опциональный источник (Chocolatey) — при желании нажмите «Установить компоненты».");
                Dispatcher.Invoke(() => btnInstallMissing.Visibility = Visibility.Visible);
            }
            else
            {
                AddLog("✅ Все компоненты в порядке.");
                Dispatcher.Invoke(() => btnInstallMissing.Visibility = Visibility.Collapsed);
            }
        }

        private async void BtnInstallMissing_Click(object sender, RoutedEventArgs e)
        {
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
                var psi = new ProcessStartInfo
                {
                    FileName               = "winget.exe",
                    Arguments              = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };
                using var process = Process.Start(psi);
                if (process == null) return (false, null, false);

                var stderrTask = process.StandardError.ReadToEndAsync();
                string output  = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
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
            if (exeName != null)
            {
                // Освобождаем мьютекс единственного экземпляра ДО запуска повышенной
                // копии — иначе она может стартовать, пока текущий процесс ещё держит
                // мьютекс (ожидание подтверждения UAC), и выйти как "уже запущен".
                App.ReleaseSingleInstanceMutex();
                var psi = new ProcessStartInfo { FileName = exeName, UseShellExecute = true, Verb = "runas" };
                try { Process.Start(psi); } catch { }
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
                btnCancelDownload.Visibility = Visibility.Collapsed;
                btnLaunchApp.IsEnabled    = false;
            });

            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var ct = timeoutCts.Token;

                var json    = await _httpClient.GetStringAsync("https://api.github.com/repos/microsoft/winget-cli/releases/latest", ct);
                var release = Newtonsoft.Json.Linq.JObject.Parse(json);

                string? msixUrl = release["assets"]?
                    .FirstOrDefault(a => a["name"]?.ToString().EndsWith(".msixbundle") == true &&
                                         a["name"]?.ToString().Contains("DesktopAppInstaller") == true)?
                    ["browser_download_url"]?.ToString();

                if (msixUrl == null) { AddLog("❌ Не удалось найти файл установки winget в последнем релизе"); return; }

                // Защита от подмены: качаем только с доверенных доменов по HTTPS
                if (!DownloadValidator.IsAllowedDownloadHost(msixUrl))
                {
                    AddLog($"⛔ Недоверенный URL загрузки winget — скачивание отменено: {msixUrl}");
                    return;
                }

                AddLog("⬇️ Скачивание зависимостей...");
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "Скачивание зависимостей...");

                var vcLibsTask = DownloadFileAsync(_httpClient,
                    "https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx", tempVcLibs, ct);
                var uiXamlTask = DownloadFileAsync(_httpClient,
                    "https://github.com/microsoft/microsoft-ui-xaml/releases/download/v2.8.6/Microsoft.UI.Xaml.2.8.x64.appx",
                    tempUiXaml, ct);

                await Task.WhenAll(vcLibsTask, uiXamlTask);

                AddLog($"⬇️ Скачивание winget ({msixUrl.Split('/').Last()})...");
                using var resp = await _httpClient.GetAsync(msixUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                // После редиректов хост мог измениться — проверяем итоговый URL
                if (!DownloadValidator.IsAllowedDownloadHostAfterRedirect(resp))
                {
                    AddLog("⛔ Загрузка winget перенаправлена на недоверенный хост — скачивание отменено");
                    return;
                }

                var total = resp.Content.Headers.ContentLength ?? -1L;
                var read  = 0L;
                var buf   = new byte[81920];

                using (var fs = new FileStream(tempMsix, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                using (var stream = await resp.Content.ReadAsStreamAsync(ct))
                {
                    int bytes;
                    while ((bytes = await stream.ReadAsync(buf.AsMemory(), ct)) > 0)
                    {
                        await fs.WriteAsync(buf.AsMemory(0, bytes), ct);
                        read += bytes;
                        if (total > 0)
                        {
                            var pct = (int)((double)read / total * 100);
                            Dispatcher.Invoke(() => { progressDownload.Value = pct; txtDownloadStatus.Text = $"Winget: {pct}%"; });
                        }
                    }
                    await fs.FlushAsync(ct);
                }

                // Целостность: скачаны с доверенных хостов по HTTPS, но перед elevated-
                // установкой дополнительно проверяем, что файлы подписаны Microsoft —
                // допускает штатные обновления содержимого по тем же URL, в отличие
                // от жёсткого SHA256-пиннинга.
                foreach (var (path, label) in new[] { (tempVcLibs, "VCLibs"), (tempUiXaml, "UI.Xaml"), (tempMsix, "winget") })
                {
                    if (!AuthenticodeVerifier.IsSignedByMicrosoft(path, out string sigError))
                    {
                        AddLog($"⛔ Подлинность пакета {label} не подтверждена ({sigError}) — установка отменена");
                        Dispatcher.Invoke(() => txtDownloadStatus.Text = "Подлинность не подтверждена");
                        return;
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

                    var psi = new ProcessStartInfo
                    {
                        FileName               = "powershell.exe",
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
                            Process.Start(new ProcessStartInfo("shutdown", "/r /t 10") { UseShellExecute = true });
                    }
                    else
                    {
                        // Автоматический вызов из setup: не показываем диалог и не
                        // предлагаем перезагрузку — только сообщаем в лог.
                        AddLog("ℹ️ Winget установлен из setup; для применения может потребоваться перезагрузка — выполните её вручную при необходимости.");
                    }
                }
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
                Dispatcher.Invoke(() => { progressDownload.Value = 0; btnLaunchApp.IsEnabled = true; });
            }
        }

        private async Task DownloadFileAsync(System.Net.Http.HttpClient http, string url, string dest, CancellationToken ct = default)
        {
            try
            {
                // Качаем только с доверенных доменов, включая итоговый URL после редиректов
                if (!DownloadValidator.IsAllowedDownloadHost(url))
                {
                    AddLog($"⛔ Недоверенный URL загрузки зависимости: {url}");
                    return;
                }
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                if (!DownloadValidator.IsAllowedDownloadHostAfterRedirect(resp))
                {
                    AddLog($"⛔ Загрузка перенаправлена на недоверенный хост: {url}");
                    return;
                }
                var data = await resp.Content.ReadAsByteArrayAsync(ct);
                await File.WriteAllBytesAsync(dest, data, ct);
            }
            catch (Exception ex)
            {
                AddLog($"⚠ Ошибка скачивания зависимости {url}: {ex.Message}");
            }
        }

        private bool IsRunAsAdmin()
        {
            var identity  = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        // Запущен ли клиент Ven4Tools из текущей папки установки.
        // Если MainModule недоступен — считаем запущенным: безопаснее показать
        // предупреждение лишний раз, чем оставить папку клиента в битом состоянии.
        private bool IsClientRunning()
        {
            try
            {
                string clientExe = Path.Combine(_clientPath, "Ven4Tools.exe");
                foreach (var proc in Process.GetProcessesByName("Ven4Tools"))
                {
                    try
                    {
                        string? exePath = proc.MainModule?.FileName;
                        if (string.IsNullOrEmpty(exePath)) { proc.Dispose(); return true; }
                        if (string.Equals(exePath, clientExe, StringComparison.OrdinalIgnoreCase)) { proc.Dispose(); return true; }
                    }
                    catch { proc.Dispose(); return true; }
                    finally { proc.Dispose(); }
                }
            }
            catch { }
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
            string tempFile = Path.Combine(Path.GetTempPath(), $"ven4_{Guid.NewGuid():N}_MicrosoftEdgeWebview2Setup.exe");
            const string webView2Url = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
            AddLog("⬇️ Скачивание WebView2 Runtime...");
            Dispatcher.Invoke(() => { progressDownload.Value = 0; txtDownloadStatus.Text = "WebView2: скачивание..."; btnLaunchApp.IsEnabled = false; });
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var ct = timeoutCts.Token;

                // Защита от подмены: качаем только с доверенных доменов по HTTPS
                if (!DownloadValidator.IsAllowedDownloadHost(webView2Url))
                {
                    AddLog("⛔ Недоверенный URL загрузки WebView2 — скачивание отменено");
                    return;
                }

                using var resp = await _httpClient.GetAsync(webView2Url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                if (!DownloadValidator.IsAllowedDownloadHostAfterRedirect(resp))
                {
                    AddLog("⛔ Загрузка WebView2 перенаправлена на недоверенный хост — скачивание отменено");
                    return;
                }
                var data = await resp.Content.ReadAsByteArrayAsync(ct);
                await File.WriteAllBytesAsync(tempFile, data, ct);

                AddLog("📦 Установка WebView2 Runtime...");
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "WebView2: установка...");
                // Без прав администратора — точечная элевация через UAC (Verb = runas)
                bool needElevation = !IsRunAsAdmin();
                var psi = new ProcessStartInfo
                {
                    FileName = tempFile, Arguments = "/silent /install",
                    UseShellExecute = needElevation, CreateNoWindow = !needElevation
                };
                if (needElevation) psi.Verb = "runas";
                using var proc = Process.Start(psi);
                if (proc != null) await proc.WaitForExitAsync();
                Dispatcher.Invoke(() => { progressDownload.Value = 100; txtDownloadStatus.Text = "WebView2: готово"; });
                AddLog("✅ WebView2 Runtime установлен");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка установки WebView2: {ex.Message}");
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "Ошибка");
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                Dispatcher.Invoke(() => { progressDownload.Value = 0; btnLaunchApp.IsEnabled = true; });
            }
        }

        private async Task InstallVcRedistAsync()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"ven4_{Guid.NewGuid():N}_vc_redist.x64.exe");
            const string vcRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
            AddLog("⬇️ Скачивание Visual C++ Redistributable 2015-2022 x64...");
            Dispatcher.Invoke(() => { progressDownload.Value = 0; txtDownloadStatus.Text = "VC++: скачивание..."; btnLaunchApp.IsEnabled = false; });
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var ct = timeoutCts.Token;

                // Защита от подмены: качаем только с доверенных доменов по HTTPS
                if (!DownloadValidator.IsAllowedDownloadHost(vcRedistUrl))
                {
                    AddLog("⛔ Недоверенный URL загрузки VC++ — скачивание отменено");
                    return;
                }

                using var resp = await _httpClient.GetAsync(vcRedistUrl,
                    HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                if (!DownloadValidator.IsAllowedDownloadHostAfterRedirect(resp))
                {
                    AddLog("⛔ Загрузка VC++ перенаправлена на недоверенный хост — скачивание отменено");
                    return;
                }

                var total = resp.Content.Headers.ContentLength ?? -1L;
                var read  = 0L;
                var buf   = new byte[81920];
                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                using (var stream = await resp.Content.ReadAsStreamAsync(ct))
                {
                    int bytes;
                    while ((bytes = await stream.ReadAsync(buf.AsMemory(), ct)) > 0)
                    {
                        await fs.WriteAsync(buf.AsMemory(0, bytes), ct);
                        read += bytes;
                        if (total > 0)
                        {
                            var pct = (int)((double)read / total * 100);
                            Dispatcher.Invoke(() => { progressDownload.Value = pct; txtDownloadStatus.Text = $"VC++: {pct}%"; });
                        }
                    }
                    await fs.FlushAsync(ct);
                }

                AddLog("📦 Установка Visual C++ Redistributable...");
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "VC++: установка...");
                // Без прав администратора — точечная элевация через UAC (Verb = runas)
                bool needElevation = !IsRunAsAdmin();
                var psi = new ProcessStartInfo
                {
                    FileName = tempFile, Arguments = "/install /quiet /norestart",
                    UseShellExecute = needElevation, CreateNoWindow = !needElevation
                };
                if (needElevation) psi.Verb = "runas";
                using var proc = Process.Start(psi);
                if (proc != null) await proc.WaitForExitAsync();
                Dispatcher.Invoke(() => { progressDownload.Value = 100; txtDownloadStatus.Text = "VC++: готово"; });
                AddLog("✅ Visual C++ Redistributable установлен");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка установки VC++: {ex.Message}");
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "Ошибка");
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                Dispatcher.Invoke(() => { progressDownload.Value = 0; btnLaunchApp.IsEnabled = true; });
            }
        }

        private static CrashReport? ReadCrashReport()
        {
            try
            {
                string path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Ven4Tools", "crash_last.json");
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
