// Services/LauncherUpdateService.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Сервис обновления установленного лаунчера (схема 2.0).
    ///
    /// Лаунчер 2.0 ставится установщиком в %LOCALAPPDATA%\Ven4Tools\Launcher\
    /// и регистрируется в «Программы и компоненты». Самообновление работает так:
    ///   1. Новый exe скачивается в %TEMP%\ven4tools_update\Ven4Tools.Launcher.exe;
    ///   2. В %TEMP% создаётся update.bat: ждёт завершения текущего процесса,
    ///      копирует новый exe в папку установки, запускает его и удаляет себя;
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

        /// <summary>Папка для скачанного обновления: %TEMP%\ven4tools_update.</summary>
        public static string UpdateStagingDir { get; } = Path.Combine(
            Path.GetTempPath(), "ven4tools_update");

        // Один HttpClient на всё время жизни процесса — стандартная практика.
        // Таймаут 10 минут: exe лаунчера ~70 МБ, на медленном канале нужен запас.
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
                    // Релиз новее, но exe-ассета лаунчера в нём нет — обновлять нечем.
                    Log($"В релизе {info.LatestVersion} нет exe-ассета лаунчера — обновление пропущено.");
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
        /// Скачивает новый exe лаунчера и запускает update.bat, который после
        /// завершения текущего процесса заменит exe в папке установки и
        /// перезапустит лаунчер.
        ///
        /// При результате true вызывающий код ОБЯЗАН завершить приложение
        /// (иначе bat не сможет перезаписать занятый exe и через ~15 секунд
        /// тихо сдастся, удалив только себя).
        /// </summary>
        /// <param name="updateInfo">Готовая информация об обновлении; если null — проверяется заново.</param>
        /// <param name="expectedSha256">Ожидаемый SHA256 exe (из version.json CDN). Если задан — после
        /// скачивания проверяется целостность; при несовпадении обновление отменяется.</param>
        public async Task<bool> DownloadAndApplyUpdateAsync(UpdateInfo? updateInfo = null, string? expectedSha256 = null)
        {
            try
            {
                updateInfo ??= await CheckForUpdateAsync();
                if (updateInfo == null || !updateInfo.HasUpdate) return false;
                if (string.IsNullOrEmpty(updateInfo.DownloadUrl)) return false;

                // Защита от подмены: качаем только с доверенных доменов GitHub.
                if (!DownloadValidator.IsAllowedDownloadHost(updateInfo.DownloadUrl))
                {
                    Log($"Недоверенный URL обновления — скачивание отменено: {updateInfo.DownloadUrl}");
                    return false;
                }

                Log($"Скачивание лаунчера {updateInfo.LatestVersion}...");

                Directory.CreateDirectory(UpdateStagingDir);
                string stagedExe = Path.Combine(UpdateStagingDir, ExeName);
                long bytesRead = await DownloadFileAsync(updateInfo.DownloadUrl, stagedExe);

                // Контроль целостности: размер из API релиза должен совпасть с фактическим.
                if (updateInfo.FileSize > 0 && bytesRead != updateInfo.FileSize)
                    throw new IOException(
                        $"Размер скачанного файла ({bytesRead} байт) не совпадает с ожидаемым ({updateInfo.FileSize} байт).");

                // Однофайловый self-contained exe не бывает крошечным —
                // отсекаем страницы ошибок и обрезанные загрузки.
                if (bytesRead < 1024 * 1024)
                    throw new IOException($"Скачанный файл подозрительно мал ({bytesRead} байт) — обновление отменено.");

                // Верификация SHA256, если хеш известен (CDN отдаёт его в version.json).
                if (!string.IsNullOrEmpty(expectedSha256))
                {
                    string actual = await Task.Run(() => ComputeSha256(stagedExe));
                    if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(stagedExe); } catch { }
                        throw new IOException("Контрольная сумма не совпала. Файл повреждён или подменён.");
                    }
                    Log("Целостность подтверждена (SHA256).");
                }

                // Создаём и запускаем bat-скрипт замены exe.
                string batPath = Path.Combine(Path.GetTempPath(), $"ven4tools_update_{Guid.NewGuid():N}.bat");
                File.WriteAllText(batPath, BuildUpdateBat(), new UTF8Encoding(false));

                Process.Start(new ProcessStartInfo
                {
                    FileName = batPath,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetTempPath()
                });

                Log("Скрипт обновления запущен. Лаунчер перезапустится через несколько секунд.");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Ошибка обновления лаунчера: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Содержимое update.bat. Пути заданы через переменные окружения cmd —
        /// скрипт не зависит от кодировки и имени пользователя (в т.ч. кириллицы).
        /// </summary>
        private static string BuildUpdateBat()
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("rem Скрипт обновления Ven4Tools Launcher. Создаётся автоматически и удаляет сам себя.");
            sb.AppendLine("timeout /t 2 /nobreak >nul");
            sb.AppendLine();
            sb.AppendLine("set \"SRC=%TEMP%\\ven4tools_update\"");
            sb.AppendLine("set \"DST=%LOCALAPPDATA%\\Ven4Tools\\Launcher\"");
            sb.AppendLine("if not exist \"%DST%\" mkdir \"%DST%\"");
            sb.AppendLine();
            sb.AppendLine("rem До 15 попыток с паузой 1 сек: ждём, пока старый процесс освободит exe.");
            sb.AppendLine("set ATTEMPTS=0");
            sb.AppendLine(":retry");
            sb.AppendLine("copy /y \"%SRC%\\*.exe\" \"%DST%\\\" >nul 2>&1");
            sb.AppendLine("if %errorlevel%==0 goto ok");
            sb.AppendLine("set /a ATTEMPTS+=1");
            sb.AppendLine("if %ATTEMPTS% geq 15 goto cleanup");
            sb.AppendLine("timeout /t 1 /nobreak >nul");
            sb.AppendLine("goto retry");
            sb.AppendLine();
            sb.AppendLine(":ok");
            sb.AppendLine("rmdir /s /q \"%SRC%\" >nul 2>&1");
            sb.AppendLine("start \"\" \"%DST%\\Ven4Tools.Launcher.exe\"");
            sb.AppendLine();
            sb.AppendLine(":cleanup");
            sb.AppendLine("del \"%~f0\"");
            return sb.ToString();
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

                // Ищем ассет установщика (Ven4Tools.Setup-X.Y.Z.exe).
                // GetLatestRelease() не подходит: если после launcher-релиза вышел
                // клиентский релиз, он становится «latest» и не содержит Setup.exe.
                using var gitHub = new GitHubService();
                var allReleases = await gitHub.GetAllReleases();
                var release = allReleases.FirstOrDefault(r =>
                    !r.prerelease &&
                    r.assets?.Any(a =>
                        a.name != null &&
                        a.name.StartsWith("Ven4Tools.Setup", StringComparison.OrdinalIgnoreCase) &&
                        a.name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) == true);

                var setupAsset = release?.assets?.FirstOrDefault(a =>
                    a.name != null &&
                    a.name.StartsWith("Ven4Tools.Setup", StringComparison.OrdinalIgnoreCase) &&
                    a.name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

                if (setupAsset?.browser_download_url == null ||
                    !DownloadValidator.IsAllowedDownloadHost(setupAsset.browser_download_url))
                {
                    Log("Установщик в последнем релизе не найден — продолжаем в переносном режиме.");
                    MessageBox.Show(
                        "Не удалось найти установщик в последнем релизе.\n" +
                        "Лаунчер продолжит работу в переносном режиме.",
                        "Ven4Tools Launcher",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }

                // name гарантированно не null — это условие фильтра FirstOrDefault выше.
                // Path.GetFileName отсекает возможные разделители путей в имени ассета
                // из GitHub API — защита от path injection при формировании пути в %TEMP%.
                string setupName = Path.GetFileName(setupAsset.name ?? "Ven4Tools.Setup.exe");
                if (string.IsNullOrWhiteSpace(setupName)) setupName = "Ven4Tools.Setup.exe";
                Log($"Скачивание установщика {setupName}...");
                string setupPath = Path.Combine(Path.GetTempPath(), setupName);
                long bytesRead = await DownloadFileAsync(setupAsset.browser_download_url, setupPath);

                if (setupAsset.size > 0 && bytesRead != setupAsset.size)
                    throw new IOException(
                        $"Загрузка установщика неполная: получено {bytesRead} из {setupAsset.size} байт.");

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
        /// Потоковое скачивание файла с проверкой полноты по Content-Length.
        /// Возвращает число фактически записанных байт.
        /// </summary>
        private static async Task<long> DownloadFileAsync(string url, string destinationPath)
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? -1L;
            long bytesRead = 0L;
            var buffer = new byte[81920];

            using (var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                using var stream = await response.Content.ReadAsStreamAsync();
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory())) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read));
                    bytesRead += read;
                }
                await fs.FlushAsync();
            }

            if (totalBytes > 0 && bytesRead != totalBytes)
                throw new IOException(
                    $"Загрузка неполная: получено {bytesRead} из {totalBytes} байт. Проверьте соединение.");

            return bytesRead;
        }

        /// <summary>
        /// Вычисляет SHA256 файла в виде hex-строки нижнего регистра.
        /// Читает потоково — память не зависит от размера файла.
        /// </summary>
        private static string ComputeSha256(string path)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(path);
            byte[] hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
