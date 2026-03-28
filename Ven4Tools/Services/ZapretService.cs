using System;
using System.Diagnostics;
using System.IO;
using System.Linq;           // ← ДОБАВИТЬ ЭТУ СТРОКУ
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Models;
using System.IO.Compression;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Упрощённый сервис для работы с zapret-discord-youtube.
    /// </summary>
    
    public class ZapretService
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        
        private readonly string _basePath;
        private readonly string _servicePath;
        private readonly Action<string> _log;
        
        private const string GITHUB_API_URL = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest";
        private const int MIN_REQUIRED_SPACE_MB = 200;
        
        public ZapretService(Action<string> log)
        {
            _log = log ?? (msg => { });
            _basePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ven4Tools", "zapret");
            _servicePath = Path.Combine(_basePath, "service.bat");
            
            if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools");
                _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
                _httpClient.Timeout = TimeSpan.FromSeconds(40);
            }
        }
        
        private void Log(string msg) => _log(msg);
        
        public bool IsInstalled => Directory.Exists(_basePath) && File.Exists(_servicePath);
        public string InstallPath => _basePath;
        
        private async Task<string?> GetLatestReleaseUrlAsync()
        {
            try
            {
                Log("🔍 Запрос последнего релиза zapret с GitHub API...");
                
                var response = await _httpClient.GetAsync(GITHUB_API_URL);
                if (!response.IsSuccessStatusCode)
                {
                    Log($"⚠️ GitHub API вернул {response.StatusCode}");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        if (asset.TryGetProperty("browser_download_url", out var urlProp) &&
                            asset.TryGetProperty("name", out var nameProp))
                        {
                            string name = nameProp.GetString() ?? "";
                            string url = urlProp.GetString() ?? "";

                            if (name.Contains("zapret-discord-youtube", StringComparison.OrdinalIgnoreCase) &&
                                name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                                !name.Contains("source", StringComparison.OrdinalIgnoreCase))
                            {
                                Log($"✅ Найден релизный архив: {name}");
                                return url;
                            }
                        }
                    }
                }

                Log("⚠️ Не удалось найти подходящий .zip в assets релиза");
                return null;
            }
            catch (Exception ex)
            {
                Log($"❌ Ошибка получения ссылки: {ex.Message}");
                return null;
            }
        }
        
        private bool HasEnoughSpace()
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(_basePath)!);
                long freeMB = drive.AvailableFreeSpace / 1024 / 1024;
                bool enough = freeMB >= MIN_REQUIRED_SPACE_MB;
                if (!enough) Log($"⚠️ Недостаточно места: {freeMB} MB, требуется {MIN_REQUIRED_SPACE_MB} MB");
                return enough;
            }
            catch { return true; }
        }
        
public async Task<bool> InstallAsync()
{
    Log("📥 Начинаем установку zapret...");

    try
    {
        if (!HasEnoughSpace())
        {
            MessageBox.Show(
                $"Недостаточно места на диске. Требуется минимум {MIN_REQUIRED_SPACE_MB} МБ.",
                "Недостаточно места",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
        
        Log("📥 Установка zapret-discord-youtube...");
        Directory.CreateDirectory(_basePath);
        
        string? downloadUrl = await GetLatestReleaseUrlAsync();
        if (string.IsNullOrEmpty(downloadUrl))
        {
            downloadUrl = "https://github.com/Flowseal/zapret-discord-youtube/archive/refs/heads/main.zip";
            Log("⚠️ Не удалось получить ссылку на релиз через API, используем main.zip как fallback");
        }
        
        string tempZip = Path.Combine(Path.GetTempPath(), $"zapret_{Guid.NewGuid()}.zip");
        string extractPath = Path.Combine(Path.GetTempPath(), $"zapret_extract_{Guid.NewGuid()}");
        
        Log($"📥 Скачивание: {downloadUrl}");
        
        // Скачивание
        using (var response = await _httpClient.GetAsync(downloadUrl))
        {
            response.EnsureSuccessStatusCode();
            
            using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                await response.Content.CopyToAsync(fs);
                await fs.FlushAsync();
            }
        }
        
        await Task.Delay(500);
        
        Log("📦 Распаковка...");
        
        // Распаковка
        bool extracted = false;
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(extractPath))
                    Directory.Delete(extractPath, true);
                
                ZipFile.ExtractToDirectory(tempZip, extractPath, true);
                extracted = true;
                break;
            }
            catch (IOException)
            {
                Log($"⚠️ Файл занят, попытка распаковки {i + 1}/5...");
                await Task.Delay(1000);
            }
        }
        
        if (!extracted)
        {
            throw new IOException("Не удалось распаковать архив после 5 попыток");
        }
        
// 🔥 НАХОДИМ КОРНЕВУЮ ПАПКУ (где лежит service.bat)
var extractedFolders = Directory.GetDirectories(extractPath);
Log($"📁 Найдено папок в распакованном архиве: {extractedFolders.Length}");

// Выводим все папки для отладки
for (int i = 0; i < extractedFolders.Length; i++)
{
    string folderName = Path.GetFileName(extractedFolders[i]);
    Log($"   Папка {i + 1}: {folderName}");
}

string? rootFolder = null;

// Сначала проверяем корневую папку extractPath (там может быть service.bat)
if (File.Exists(Path.Combine(extractPath, "service.bat")))
{
    rootFolder = extractPath;
    Log($"✅ Найден service.bat в корне распакованной папки");
}
else
{
    // Ищем папку, в которой есть service.bat
    foreach (var folder in extractedFolders)
    {
        if (File.Exists(Path.Combine(folder, "service.bat")))
        {
            rootFolder = folder;
            Log($"✅ Найден service.bat в папке: {Path.GetFileName(folder)}");
            break;
        }
    }
}

// Если не нашли папку с service.bat, ищем service.bat рекурсивно
if (rootFolder == null)
{
    Log("⚠️ service.bat не найден в корне и в первых папках, ищем рекурсивно...");
    
    var allFiles = Directory.GetFiles(extractPath, "service.bat", SearchOption.AllDirectories);
    if (allFiles.Any())
    {
        rootFolder = Path.GetDirectoryName(allFiles.First());
        Log($"✅ Найден service.bat в: {rootFolder}");
    }
}

// Если всё ещё не нашли — берём первую папку и надеемся на лучшее
if (rootFolder == null)
{
    if (extractedFolders.Length > 0)
    {
        rootFolder = extractedFolders.First();
        Log($"⚠️ Не найдена папка с service.bat, берём первую: {rootFolder}");
    }
    else
    {
        rootFolder = extractPath;
        Log($"⚠️ Нет папок, копируем корневую папку: {rootFolder}");
    }
}

Log($"📁 Копирование из {rootFolder} в {_basePath}...");
Log($"📁 Содержимое исходной папки: {string.Join(", ", Directory.GetFiles(rootFolder).Select(Path.GetFileName).Take(10))}");

CopyDirectoryWithRetry(rootFolder, _basePath);
        
        // 🔥 ПРОВЕРЯЕМ, ЧТО service.bat НА МЕСТЕ
        Log("📁 Проверка результата...");
        if (File.Exists(_servicePath))
        {
            Log($"✅ service.bat найден по адресу: {_servicePath}");
        }
        else
        {
            Log($"❌ service.bat НЕ НАЙДЕН!");
            var foundBats = Directory.GetFiles(_basePath, "service.bat", SearchOption.AllDirectories);
            if (foundBats.Any())
            {
                Log($"   Найден в: {foundBats.First()}");
            }
            else
            {
                Log("   service.bat отсутствует во всех подпапках");
            }
        }
        
        // Очистка временных файлов
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (File.Exists(tempZip))
                    File.Delete(tempZip);
                if (Directory.Exists(extractPath))
                    Directory.Delete(extractPath, true);
                break;
            }
            catch (IOException)
            {
                Log($"⚠️ Файл занят, попытка очистки {i + 1}/5...");
                await Task.Delay(1000);
            }
        }
        
        Log("✅ Zapret установлен");
        return true;
    }
    catch (Exception ex)
    {
        Log($"❌ Ошибка установки: {ex.Message}");
        MessageBox.Show(
            $"Ошибка установки: {ex.Message}\n\n" +
            "Попробуйте установить вручную: https://github.com/Flowseal/zapret-discord-youtube",
            "Ошибка установки",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        return false;
    }
}
        
        private void CopyDirectoryWithRetry(string source, string destination, int maxRetries = 3)
        {
            Log("📁 Проверка распакованных файлов...");
if (Directory.Exists(_basePath))
{
    var files = Directory.GetFiles(_basePath, "*", SearchOption.AllDirectories).Take(10);
    Log($"   Найдено файлов: {Directory.GetFiles(_basePath, "*", SearchOption.AllDirectories).Length}");
    foreach (var file in files)
    {
        Log($"     {Path.GetFileName(file)}");
    }
}
else
{
    Log("❌ Папка назначения не существует!");
}
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                    {
                        string targetDir = dir.Replace(source, destination);
                        Directory.CreateDirectory(targetDir);
                    }
                    
                    foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                    {
                        string targetFile = file.Replace(source, destination);
                        File.Copy(file, targetFile, true);
                    }
                    return;
                }
                catch (IOException) when (attempt < maxRetries)
                {
                    Log($"⚠️ Копирование не удалось, попытка {attempt}/{maxRetries}...");
                    System.Threading.Thread.Sleep(500);
                }
            }
        }
        
        public void OpenServiceMenu()
        {
            if (!IsInstalled)
            {
                MessageBox.Show("Zapret не установлен. Сначала нажмите 'Установить zapret'.",
                    "Не установлено", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _servicePath,
                    UseShellExecute = true,
                    Verb = "runas"
                });
                Log("📟 Открыто меню service.bat");
            }
            catch (Exception ex)
            {
                Log($"❌ Ошибка открытия service.bat: {ex.Message}");
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
public async Task<bool> RemoveAsync()
{
    if (!IsInstalled) return true;
    
    try
    {
        Log("🗑️ Удаление zapret...");
        
        // Удаляем службы без открытия окон
        var psi = new ProcessStartInfo
        {
            FileName = _servicePath,
            Arguments = "2",
            UseShellExecute = false,      // ← НЕ используем шелл
            CreateNoWindow = true,        // ← НЕ создаём окно
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        
        using var process = Process.Start(psi);
        if (process != null)
        {
            // Ждём завершения с таймаутом
            var timeout = Task.Delay(30000);
            var exit = process.WaitForExitAsync();
            
            var completed = await Task.WhenAny(exit, timeout);
            if (completed == timeout)
            {
                Log("⚠️ Таймаут удаления служб, продолжаем...");
                try { process.Kill(); } catch { }
            }
            
            // Логируем вывод для отладки
            string output = await process.StandardOutput.ReadToEndAsync();
            if (!string.IsNullOrEmpty(output))
                Log($"   Вывод: {output.Trim()}");
        }
        
        await Task.Delay(2000);
        
        // Удаляем папку с повторными попытками
        bool deleteSuccess = false;
        int retries = 3;
        for (int i = 0; i < retries; i++)
        {
            try
            {
                if (Directory.Exists(_basePath))
                    Directory.Delete(_basePath, true);
                deleteSuccess = true;
                break;
            }
            catch (IOException) when (i < retries - 1)
            {
                Log($"⚠️ Не удалось удалить папку, попытка {i + 1}/{retries}...");
                await Task.Delay(1000);
            }
        }
        
        if (deleteSuccess)
        {
            Log("✅ Zapret удалён");
            MessageBox.Show(
                "✅ Zapret удалён.\n\n" +
                "Для полного удаления драйвера WinDivert рекомендуется перезагрузить компьютер.",
                "Удаление завершено",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            Log("⚠️ Папка не удалилась автоматически");
            MessageBox.Show(
                "⚠️ Zapret частично удалён.\n\n" +
                "Папка осталась, удалите её вручную:\n" +
                $"{_basePath}\n\n" +
                "Для полного удаления драйвера WinDivert перезагрузите компьютер.",
                "Требуется ручное удаление",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        
        return deleteSuccess;
    }
    catch (Exception ex)
    {
        Log($"❌ Ошибка удаления: {ex.Message}");
        MessageBox.Show(
            $"Ошибка удаления: {ex.Message}\n\n" +
            "Попробуйте удалить папку вручную:\n" +
            $"{_basePath}",
            "Ошибка удаления",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        return false;
    }
}
        
public void OpenInstallFolder()
{
    if (!IsInstalled)
    {
        MessageBox.Show("Zapret не установлен.", "Не установлено", MessageBoxButton.OK, MessageBoxImage.Warning);
        return;
    }
    
    try
    {
        Process.Start("explorer.exe", _basePath);
        Log($"📁 Открыта папка: {_basePath}");
    }
    catch (Exception ex)
    {
        Log($"❌ Ошибка открытия папки: {ex.Message}");
    }
}
        
        public void OpenDocumentation()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/Flowseal/zapret-discord-youtube",
                    UseShellExecute = true
                });
                Log("📖 Открыта документация zapret");
            }
            catch (Exception ex)
            {
                Log($"❌ Ошибка открытия документации: {ex.Message}");
            }
        }
    }
}