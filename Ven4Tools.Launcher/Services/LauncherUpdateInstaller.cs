// Services/LauncherUpdateInstaller.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Скачивание установщика Ven4Tools.Setup-X.Y.Z.exe и его запуск — как в режиме
    /// тихого самообновления, так и в режиме первичной установки переносного лаунчера.
    ///
    /// Вторая половина обновления лаунчера: решение «что качать» принимает
    /// <see cref="LauncherUpdateChecker"/>, здесь только загрузка по цепочке
    /// источников, обязательная сверка SHA256 (fail-closed) и запуск процесса.
    /// Зависимость односторонняя: установка обращается к проверке, обратной связи нет.
    /// </summary>
    internal sealed class LauncherUpdateInstaller
    {
        // Один HttpClient на всё время жизни процесса — стандартная практика.
        // Таймаут 10 минут: установщик лаунчера ~30 МБ, на медленном канале нужен запас.
        private static readonly HttpClient _httpClient = CreateClient();

        private readonly Action<string>? _log;
        private readonly DownloadSource _preference;
        private readonly LauncherUpdateChecker _checker;

        internal LauncherUpdateInstaller(
            LauncherUpdateChecker checker,
            Action<string>? log = null,
            DownloadSource preference = DownloadSource.Auto)
        {
            _checker = checker;
            _log = log;
            _preference = preference;
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
        /// Разворачивает ссылки установщика из UpdateInfo в упорядоченную цепочку
        /// кандидатов с транспортами (обычный клиент + IP-pinned для варианта «прямой IP»).
        /// IP для pinning — последний известный cdn_ip, иначе резервный FallbackCdnIp.
        /// </summary>
        private List<DownloadCandidate> BuildSetupCandidates(UpdateInfo info)
        {
            string ip = CdnService.LastKnownCdnIp ?? IpPinnedHttpClientFactory.FallbackCdnIp;
            HttpClient ipPinned = IpPinnedHttpClientFactory.GetOrCreate(ip, TimeSpan.FromMinutes(10));
            return FallbackDownloader.BuildCandidates(
                _preference,
                info.SetupCdnUrl,
                info.SetupMirrorHostingUrl,
                info.SetupGithubUrl ?? info.DownloadUrl,
                _httpClient,
                ipPinned);
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
        internal async Task<bool> DownloadAndRunSetupUpdateAsync(UpdateInfo? updateInfo = null)
        {
            string? stagingDir = null;
            try
            {
                updateInfo ??= await _checker.CheckForUpdateAsync();
                if (updateInfo == null || !updateInfo.HasUpdate) return false;
                if (string.IsNullOrEmpty(updateInfo.LatestVersion)) return false;
                if (!DownloadValidator.IsValidSha256(updateInfo.ExpectedSha256))
                {
                    Log("Обновление лаунчера отменено: контрольная сумма установщика недоступна " +
                        "(CDN не ответил или версия на CDN не совпадает с релизом).");
                    return false;
                }

                // Упорядоченная цепочка источников: CDN(домен) → CDN(IP) → Хостинг → GitHub
                // (с учётом выбранного пользователем предпочтения).
                var candidates = BuildSetupCandidates(updateInfo);
                if (candidates.Count == 0)
                {
                    Log("Обновление лаунчера отменено: нет доступных источников установщика.");
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
                // Таймаут на весь цикл скачивания (все источники): без него
                // зависший поток на ResponseHeadersRead блокирует обновление навсегда.
                var downloader = new FallbackDownloader();
                using var downloadCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                // using держит FileShare.Read-хендл на setupPath открытым до Process.Start
                // ниже — закрывает окно TOCTOU между проверкой SHA256 и запуском установщика
                // (уникальная папка защищает от заранее подложенного файла, но не от процесса
                // того же пользователя, наблюдающего за %TEMP% и подменяющего файл после его
                // появления).
                using var downloadResult = await downloader.DownloadAsync(
                    candidates,
                    setupPath,
                    downloadCts.Token,
                    updateInfo.ExpectedSha256);
                string source = downloadResult.SourceLabel;
                Log($"Целостность установщика подтверждена (SHA256), источник: {source}.");

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

        /// <summary>
        /// Если лаунчер запущен не из папки установки (например, из Downloads) —
        /// предлагает скачать и запустить установщик последней версии.
        ///
        /// Возвращает true, если установщик запущен: вызывающий код должен
        /// завершить приложение (установщик сам закроет процессы лаунчера).
        /// Возвращает false, если лаунчер уже установлен, пользователь отказался
        /// или установщик недоступен — тогда работаем в переносном режиме.
        /// </summary>
        internal async Task<bool> OfferInstallationAsync()
        {
            string? stagingDir = null;
            try
            {
                if (LauncherInstallation.IsRunningFromInstallDir()) return false;

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

                // Ищем последнюю доступную версию установщика: CDN version.json (основной
                // источник — работает даже при блокировке GitHub) с резервом на GitHub.
                // "0.0.0" — любая найденная версия считается новее, берём самую свежую.
                var latest = await _checker.ResolveSetupUpdateAsync("0.0.0");

                if (latest == null || !latest.HasUpdate || string.IsNullOrEmpty(latest.LatestVersion))
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
                if (!DownloadValidator.IsValidSha256(latest.ExpectedSha256))
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

                var candidates = BuildSetupCandidates(latest);
                if (candidates.Count == 0)
                {
                    Log("Нет доступных источников установщика — продолжаем в переносном режиме.");
                    MessageBox.Show(
                        "Не удалось найти доступный источник установщика.\n" +
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
                stagingDir = Path.Combine(Path.GetTempPath(), $"ven4tools_setup_{Guid.NewGuid():N}");
                Directory.CreateDirectory(stagingDir);
                string setupPath = Path.Combine(stagingDir, setupName);

                var downloader = new FallbackDownloader();
                using var downloadCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                // using держит FileShare.Read-хендл на setupPath открытым до Process.Start
                // ниже — закрывает окно TOCTOU между проверкой SHA256 и запуском установщика
                // (см. аналогичный фикс в DownloadAndRunSetupUpdateAsync выше).
                using var downloadResult = await downloader.DownloadAsync(
                    candidates,
                    setupPath,
                    downloadCts.Token,
                    latest.ExpectedSha256);
                string source = downloadResult.SourceLabel;
                Log($"Целостность установщика подтверждена (SHA256), источник: {source}.");

                // Заметка: при появлении сертификата подписи кода здесь дополнительно
                // проверяется Authenticode-подпись установщика перед запуском.
                Process.Start(new ProcessStartInfo
                {
                    FileName = setupPath,
                    UseShellExecute = true,
                    WorkingDirectory = stagingDir
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
                // Обрыв посреди скачивания оставляет staging-папку в %TEMP% — та же
                // очистка, что уже применяется в DownloadAndRunSetupUpdateAsync.
                TryDeleteDirectory(stagingDir);
                return false;
            }
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
