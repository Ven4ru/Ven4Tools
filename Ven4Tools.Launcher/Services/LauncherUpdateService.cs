// Services/LauncherUpdateService.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Сервис обновления установленного лаунчера (схема 2.1: только установщик).
    ///
    /// Лаунчер ставится установщиком Ven4Tools.Setup-X.Y.Z.exe в
    /// %LOCALAPPDATA%\Ven4Tools\Launcher\ и регистрируется в «Программы и
    /// компоненты». Самообновление работает через тот же установщик:
    ///   1. Setup-X.Y.Z.exe скачивается в уникальную папку %TEMP%\ven4tools_setup_&lt;guid&gt;
    ///      с обязательной проверкой SHA256 (из version.json CDN) и хоста после редиректа;
    ///   2. Установщик запускается с флагами тихого самообновления
    ///      (/S /UPDATE /WAITPID=&lt;pid&gt; /RELAUNCH — см. installer\Ven4Tools.Setup.nsi):
    ///      он дожидается завершения текущего процесса, делает бэкап exe,
    ///      ставит новую версию, проверяет её и перезапускает лаунчер
    ///      (при неудаче откатывает бэкап и запускает старую версию);
    ///   3. Текущий процесс завершается (это делает вызывающий код).
    ///
    /// Если лаунчер запущен НЕ из папки установки (например, из Downloads),
    /// OfferInstallationAsync() предлагает скачать и запустить установщик.
    /// </summary>
    public class LauncherUpdateService
    {
        /// <summary>Имя exe-файла лаунчера.</summary>
        public const string ExeName = "Ven4Tools.Launcher.exe";

        /// <summary>Папка установки лаунчера: %LOCALAPPDATA%\Ven4Tools\Launcher.</summary>
        public static string InstallDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "Launcher");

        /// <summary>Полный путь к установленному exe лаунчера.</summary>
        public static string InstalledExePath { get; } = Path.Combine(InstallDir, ExeName);

        // Один HttpClient на всё время жизни процесса — стандартная практика.
        // Таймаут 10 минут: установщик лаунчера ~30 МБ, на медленном канале нужен запас.
        private static readonly HttpClient _httpClient = CreateClient();

        private readonly Action<string>? _log;

        public LauncherUpdateService(Action<string>? log = null)
        {
            _log = log;
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools.Launcher");
            return client;
        }

        private void Log(string message)
        {
            _log?.Invoke(message);
            Debug.WriteLine(message);
        }

        /// <summary>
        /// Текущая версия лаунчера в формате X.Y.Z (из метаданных сборки).
        /// </summary>
        public static string GetCurrentVersion()
        {
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "0.0.0";
        }

        /// <summary>
        /// Путь к exe текущего процесса (или пустая строка, если определить не удалось).
        /// </summary>
        public static string GetCurrentExePath()
        {
            try
            {
                return Process.GetCurrentProcess().MainModule?.FileName ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Запущен ли лаунчер из папки установки %LOCALAPPDATA%\Ven4Tools\Launcher.
        /// </summary>
        public static bool IsRunningFromInstallDir()
        {
            string exePath = GetCurrentExePath();
            if (string.IsNullOrEmpty(exePath)) return false;

            try
            {
                string currentDir = Path.GetFullPath(Path.GetDirectoryName(exePath) ?? "")
                                        .TrimEnd(Path.DirectorySeparatorChar);
                string installDir = Path.GetFullPath(InstallDir)
                                        .TrimEnd(Path.DirectorySeparatorChar);
                return string.Equals(currentDir, installDir, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Проверка обновления лаунчера через GitHub Releases API.
        /// Возвращает null при сетевой ошибке (для вызывающего кода это «обновлений нет»).
        /// </summary>
        public async Task<UpdateInfo?> CheckForUpdateAsync()
        {
            try
            {
                using var gitHub = new GitHubService();
                var info = await gitHub.CheckLauncherUpdate(GetCurrentVersion());

                if (info == null)
                {
                    Log("Не удалось получить информацию о релизах GitHub.");
                    return null;
                }

                if (info.HasUpdate && string.IsNullOrEmpty(info.DownloadUrl))
                {
                    // Релиз новее, но установщика в нём нет — обновлять нечем.
                    Log($"В релизе {info.LatestVersion} нет установщика Ven4Tools.Setup — обновление пропущено.");
                    info.HasUpdate = false;
                }

                return info;
            }
            catch (Exception ex)
            {
                Log($"Ошибка проверки обновлений лаунчера: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Аргументы запуска установщика в режиме тихого самообновления.
        /// Обрабатываются в installer\Ven4Tools.Setup.nsi (.onInit):
        ///   /S        — тихий режим NSIS (без диалогов);
        ///   /UPDATE   — режим самообновления (бэкап, откат при неудаче);
        ///   /WAITPID= — дождаться завершения процесса лаунчера с этим PID;
        ///   /RELAUNCH — запустить лаунчер после установки (или отката).
        /// </summary>
        internal static string BuildSetupUpdateArguments(int waitPid)
        {
            return $"/S /UPDATE /WAITPID={waitPid} /RELAUNCH";
        }

        /// <summary>
        /// Имя файла установщика для версии X.Y.Z. Недопустимые для имени файла
        /// символы заменяются — версия приходит из внешних данных (тег релиза).
        /// </summary>
        internal static string BuildSetupFileName(string version)
        {
            string name = $"Ven4Tools.Setup-{version}.exe";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        /// <summary>
        /// Скачивает установщик Ven4Tools.Setup-X.Y.Z.exe и запускает его в режиме
        /// тихого самообновления: установщик дождётся завершения текущего процесса,
        /// заменит exe в папке установки (с бэкапом и откатом при неудаче) и
        /// перезапустит лаунчер.
        ///
        /// SHA256 обязателен (fail-closed): без подтверждённой контрольной суммы
        /// из version.json CDN обновление не выполняется.
        ///
        /// При результате true вызывающий код ОБЯЗАН завершить приложение —
        /// установщик ждёт завершения процесса и только потом меняет файлы.
        /// </summary>
        /// <param name="updateInfo">Готовая информация об обновлении; если null — проверяется заново.</param>
        /// <param name="expectedSha256">Ожидаемый SHA256 установщика (из version.json CDN). Обязателен.</param>
        /// <param name="fallbackUrl">Резервный URL установщика (CDN), если основной недоступен.</param>
        public async Task<bool> DownloadAndRunSetupUpdateAsync(
            UpdateInfo? updateInfo = null,
            string? expectedSha256 = null,
            string? fallbackUrl = null)
        {
            string? stagingDir = null;
            try
            {
                updateInfo ??= await CheckForUpdateAsync();
                if (updateInfo == null || !updateInfo.HasUpdate) return false;
                if (string.IsNullOrEmpty(updateInfo.DownloadUrl)) return false;
                if (string.IsNullOrEmpty(updateInfo.LatestVersion)) return false;
                if (!IsValidSha256(expectedSha256))
                {
                    Log("Обновление лаунчера отменено: контрольная сумма установщика недоступна " +
                        "(CDN не ответил или версия на CDN не совпадает с релизом).");
                    return false;
                }

                Log($"Скачивание установщика лаунчера {updateInfo.LatestVersion}...");

                // Уникальная папка на каждое обновление: никто не может заранее
                // подложить файл в известный путь (в отличие от общей папки staging).
                stagingDir = Path.Combine(Path.GetTempPath(), $"ven4tools_setup_{Guid.NewGuid():N}");
                Directory.CreateDirectory(stagingDir);
                string setupPath = Path.Combine(stagingDir, BuildSetupFileName(updateInfo.LatestVersion));

                // FallbackDownloader: проверка доверенного хоста (включая редиректы),
                // .partial-загрузка и обязательная сверка SHA256 до принятия файла.
                var downloader = new FallbackDownloader(_httpClient);
                await downloader.DownloadAsync(
                    updateInfo.DownloadUrl,
                    fallbackUrl,
                    setupPath,
                    CancellationToken.None,
                    expectedSha256);
                Log("Целостность установщика подтверждена (SHA256).");

                // Заметка: при появлении сертификата подписи кода здесь дополнительно
                // проверяется Authenticode-подпись установщика перед запуском.
                Process.Start(new ProcessStartInfo
                {
                    FileName = setupPath,
                    Arguments = BuildSetupUpdateArguments(Environment.ProcessId),
                    UseShellExecute = true,
                    WorkingDirectory = stagingDir
                });

                Log("Установщик обновления запущен. Лаунчер перезапустится через несколько секунд.");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Ошибка обновления лаунчера: {ex.Message}");
                TryDeleteDirectory(stagingDir);
                return false;
            }
        }

        internal static bool IsValidSha256(string? value)
        {
            return value?.Length == 64 && value.All(Uri.IsHexDigit);
        }

        /// <summary>
        /// Если лаунчер запущен не из папки установки (например, из Downloads) —
        /// предлагает скачать и запустить установщик последней версии.
        ///
        /// Возвращает true, если установщик запущен: вызывающий код должен
        /// завершить приложение (установщик сам закроет процессы лаунчера).
        /// Возвращает false, если лаунчер уже установлен, пользователь отказался
        /// или установщик недоступен — тогда работаем в переносном режиме.
        /// </summary>
        public async Task<bool> OfferInstallationAsync()
        {
            try
            {
                if (IsRunningFromInstallDir()) return false;

                var answer = MessageBox.Show(
                    "Лаунчер запущен из временного расположения.\n\n" +
                    "Рекомендуется установить Ven4Tools Launcher: он появится в меню «Пуск» " +
                    "и в «Программы и компоненты», будет автоматически обновляться " +
                    "и его легко удалить.\n\n" +
                    "Скачать и запустить установщик сейчас?",
                    "Установка Ven4Tools Launcher",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (answer != MessageBoxResult.Yes) return false;

                // Ищем последний стабильный релиз с установщиком Ven4Tools.Setup-X.Y.Z.exe.
                // "0.0.0" — любой найденный релиз считается новее, берём самый свежий.
                using var gitHub = new GitHubService();
                var latest = await gitHub.CheckLauncherUpdate("0.0.0");

                if (latest?.DownloadUrl == null ||
                    string.IsNullOrEmpty(latest.LatestVersion) ||
                    !DownloadValidator.IsAllowedDownloadHost(latest.DownloadUrl))
                {
                    Log("Установщик в релизах не найден — продолжаем в переносном режиме.");
                    MessageBox.Show(
                        "Не удалось найти установщик в последнем релизе.\n" +
                        "Лаунчер продолжит работу в переносном режиме.",
                        "Ven4Tools Launcher",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }

                // SHA256 обязателен (fail-closed): хеш берём из version.json CDN и только
                // если версия на CDN совпадает с версией релиза — иначе хеш от другого билда.
                (string? expectedSha256, string? fallbackUrl) =
                    await GetSetupHashFromCdnAsync(latest.LatestVersion);

                if (expectedSha256 == null)
                {
                    Log("Контрольная сумма установщика недоступна (CDN) — установка отменена.");
                    MessageBox.Show(
                        "Не удалось подтвердить целостность установщика.\n" +
                        "Лаунчер продолжит работу в переносном режиме.",
                        "Ven4Tools Launcher",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }

                string setupName = BuildSetupFileName(latest.LatestVersion);
                Log($"Скачивание установщика {setupName}...");

                // Уникальная папка: файл нельзя подменить между проверкой и запуском
                // по заранее известному пути.
                string stagingDir = Path.Combine(Path.GetTempPath(), $"ven4tools_setup_{Guid.NewGuid():N}");
                Directory.CreateDirectory(stagingDir);
                string setupPath = Path.Combine(stagingDir, setupName);

                var downloader = new FallbackDownloader(_httpClient);
                await downloader.DownloadAsync(
                    latest.DownloadUrl,
                    fallbackUrl,
                    setupPath,
                    CancellationToken.None,
                    expectedSha256);
                Log("Целостность установщика подтверждена (SHA256).");

                // Заметка: при появлении сертификата подписи кода здесь дополнительно
                // проверяется Authenticode-подпись установщика перед запуском.
                Process.Start(new ProcessStartInfo
                {
                    FileName = setupPath,
                    UseShellExecute = true
                });

                Log("Установщик запущен. Завершаем текущий процесс.");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Ошибка запуска установщика: {ex.Message}");
                MessageBox.Show(
                    $"Не удалось скачать установщик:\n{ex.Message}\n\n" +
                    "Лаунчер продолжит работу в переносном режиме.",
                    "Ven4Tools Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
        }

        /// <summary>
        /// SHA256 установщика и резервный URL из version.json CDN — только если
        /// версия лаунчера на CDN совпадает с запрошенной. Любая ошибка → (null, null):
        /// вызывающий код обязан отменить установку (fail-closed).
        /// </summary>
        private static async Task<(string? Sha256, string? FallbackUrl)> GetSetupHashFromCdnAsync(string version)
        {
            try
            {
                using var cdnService = new CdnService();
                CdnVersionInfo? cdnInfo = await cdnService.GetVersionInfoAsync();
                if (cdnInfo?.Launcher != null &&
                    string.Equals(cdnInfo.Launcher.Version, version, StringComparison.OrdinalIgnoreCase) &&
                    IsValidSha256(cdnInfo.Launcher.SetupSha256))
                {
                    return (cdnInfo.Launcher.SetupSha256, cdnInfo.Launcher.SetupUrl);
                }
            }
            catch
            {
                // CDN недоступен — хеш подтвердить нечем, установка будет отменена.
            }

            return (null, null);
        }

        private static void TryDeleteDirectory(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch
            {
                // Временная папка будет удалена системной очисткой %TEMP%.
            }
        }
    }
}
