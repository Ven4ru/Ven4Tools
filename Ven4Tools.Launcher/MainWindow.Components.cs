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

        private bool IsRunAsAdmin()
        {
            var identity  = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
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
