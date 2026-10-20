using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Ven4Tools.Services
{
    /// <summary>Как удаляется категория.</summary>
    public enum CleanupMethod
    {
        /// <summary>Штатная «Очистка диска»: StateFlags + cleanmgr /sagerun.</summary>
        CleanMgr,
        /// <summary>Удаление файлов самим клиентом (временные файлы старше суток).</summary>
        DirectFiles,
        /// <summary>Кэш скачанных обновлений: на время удаления останавливается служба обновлений.</summary>
        UpdateCache,
        /// <summary>Корзина: SHEmptyRecycleBin.</summary>
        RecycleBin,
        /// <summary>Хранилище компонентов: DISM /StartComponentCleanup (без /ResetBase).</summary>
        ComponentStore,
    }

    /// <summary>Описание категории очистки.</summary>
    public sealed record CleanupCategory(
        string Id,
        string Title,
        string Description,
        string? Warning,
        CleanupMethod Method,
        IReadOnlyList<string> CleanMgrHandlers);

    /// <summary>Результат подсчёта одной категории.</summary>
    /// <param name="Bytes">Размер; null — нет данных.</param>
    /// <param name="IsUpperBound">true — оценка сверху («до N»), а не точный размер.</param>
    /// <param name="Note">Пояснение к цифре (например, вывод DISM).</param>
    public sealed record CleanupMeasurement(long? Bytes, bool IsUpperBound, string? Note);

    /// <summary>Разобранный вывод DISM /AnalyzeComponentStore.</summary>
    public sealed record ComponentStoreAnalysis(
        long? ActualSizeBytes,
        long? BackupsBytes,
        long? CacheBytes,
        int? ReclaimablePackages,
        bool? CleanupRecommended)
    {
        /// <summary>
        /// Оценка сверху того, что может уйти: резервные копии и отключённые компоненты
        /// плюс кэш. /StartComponentCleanup без /ResetBase удаляет только заменённые
        /// версии, поэтому реально освобождается не больше этой суммы.
        /// </summary>
        public long? ReclaimableUpperBound =>
            BackupsBytes is null && CacheBytes is null ? null : (BackupsBytes ?? 0) + (CacheBytes ?? 0);
    }

    /// <summary>
    /// «Освободить место на C». Считает размер категорий сам (в фоне, с отменой),
    /// удаляет штатными средствами Windows: «Очисткой диска» (cleanmgr /sagerun со своим
    /// номером набора — после работы свои StateFlags убираются), SHEmptyRecycleBin и
    /// DISM /StartComponentCleanup. Временные файлы и кэш скачанных обновлений клиент
    /// удаляет сам: у «Очистки диска» для них нет отдельного обработчика с понятным
    /// охватом (её «Временные файлы» берут только файлы старше недели, и цифра заранее
    /// не совпала бы с удалённым). Отката нет ни у одного действия.
    /// </summary>
    public static class DiskCleanupService
    {
        /// <summary>Номер набора для cleanmgr /sagerun — свой, чтобы не трогать чужие наборы.</summary>
        public const int SageSet = 6505;
        private const string StateFlagsValue = "StateFlags6505";
        private const string VolumeCachesKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";

        /// <summary>Временные файлы моложе суток не трогаем: их может прямо сейчас использовать установщик.</summary>
        public static readonly TimeSpan TempMinAge = TimeSpan.FromHours(24);

        private static string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        private static string SystemDir => Environment.SystemDirectory;
        public static string SystemDriveRoot => Path.GetPathRoot(WindowsDir) ?? @"C:\";

        public static IReadOnlyList<CleanupCategory> Categories { get; } = new[]
        {
            new CleanupCategory("wu-cache", "Кэш обновлений Windows",
                "Скачанные установочные файлы обновлений (SoftwareDistribution\\Download). Неустановленное обновление Windows скачает заново.",
                null, CleanupMethod.UpdateCache, Array.Empty<string>()),
            new CleanupCategory("windows-old", "Предыдущая версия Windows (Windows.old)",
                "Копия прежней системы, оставшаяся после крупного обновления или переустановки.",
                "Откат к прежней версии Windows станет невозможен.",
                CleanupMethod.CleanMgr, new[] { "Previous Installations" }),
            new CleanupCategory("temp", "Временные файлы",
                "Папки временных файлов пользователя и Windows. Удаляются только файлы старше суток; занятые программами пропускаются.",
                null, CleanupMethod.DirectFiles, Array.Empty<string>()),
            new CleanupCategory("dumps", "Дампы памяти и отчёты об ошибках",
                "Снимки памяти после синих экранов (Minidump, MEMORY.DMP) и отчёты службы ошибок Windows (WER).",
                "Прошлые синие экраны больше нельзя будет разобрать — в том числе в «Диагностике».",
                CleanupMethod.CleanMgr, new[]
                {
                    "System error memory dump files", "System error minidump files",
                    "Windows Error Reporting Files", "Windows Error Reporting Archive Files",
                    "Windows Error Reporting Queue Files", "Windows Error Reporting System Archive Files",
                    "Windows Error Reporting System Queue Files", "Windows Error Reporting Temp Files",
                }),
            new CleanupCategory("delivery-optimization", "Кэш оптимизации доставки",
                "Части обновлений, которые Windows хранит, чтобы раздавать их другим компьютерам.",
                null, CleanupMethod.CleanMgr, new[] { "Delivery Optimization Files" }),
            new CleanupCategory("recycle-bin", "Корзина",
                "Файлы, удалённые в корзину на диске C.",
                "Файлы из корзины будут удалены насовсем.",
                CleanupMethod.RecycleBin, Array.Empty<string>()),
            new CleanupCategory("thumbnails", "Кэш эскизов",
                "Уменьшенные копии картинок и видео для Проводника. Создаются заново при открытии папок.",
                null, CleanupMethod.CleanMgr, new[] { "Thumbnail Cache" }),
            new CleanupCategory("winsxs", "Хранилище компонентов (WinSxS)",
                "Старые версии системных компонентов, заменённые обновлениями. Подсчёт и очистка идут через DISM и занимают несколько минут.",
                "Удалённые старые версии обновлений нельзя будет удалить или откатить.",
                CleanupMethod.ComponentStore, Array.Empty<string>()),
        };

        // ── Подсчёт ──────────────────────────────────────────────────────────

        /// <summary>Посчитать размер категории. Блокирующий вызов, кроме WinSxS — запускать в фоне.</summary>
        public static async Task<CleanupMeasurement> MeasureAsync(CleanupCategory category, CancellationToken ct)
        {
            switch (category.Id)
            {
                case "wu-cache":
                    return Exact(DirectorySize(Path.Combine(WindowsDir, "SoftwareDistribution", "Download"), null, ct));
                case "windows-old":
                    {
                        string path = Path.Combine(SystemDriveRoot, "Windows.old");
                        if (!Directory.Exists(path)) return new CleanupMeasurement(0, false, null);
                        // Часть папок Windows.old закрыта даже для администратора — цифра может быть меньше реальной.
                        return new CleanupMeasurement(DirectorySize(path, null, ct), false, null);
                    }
                case "temp":
                    {
                        long total = 0;
                        foreach (string root in TempRoots())
                            total += DirectorySize(root, TempMinAge, ct);
                        return Exact(total);
                    }
                case "dumps":
                    {
                        long total = 0;
                        foreach (string p in DumpPaths())
                        {
                            ct.ThrowIfCancellationRequested();
                            if (File.Exists(p)) total += SafeLength(p);
                            else total += DirectorySize(p, null, ct);
                        }
                        return Exact(total);
                    }
                case "delivery-optimization":
                    return Exact(DeliveryOptimizationPaths().Sum(p => DirectorySize(p, null, ct)));
                case "recycle-bin":
                    {
                        long? size = QueryRecycleBin(SystemDriveRoot);
                        return new CleanupMeasurement(size, false, null);
                    }
                case "thumbnails":
                    {
                        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Microsoft", "Windows", "Explorer");
                        long total = 0;
                        if (Directory.Exists(dir))
                            foreach (string f in Directory.EnumerateFiles(dir, "thumbcache_*.db"))
                                total += SafeLength(f);
                        return Exact(total);
                    }
                case "winsxs":
                    {
                        ComponentStoreAnalysis? a = await AnalyzeComponentStoreAsync(ct).ConfigureAwait(false);
                        if (a == null) return new CleanupMeasurement(null, false, Tr("DISM не ответил — нет данных."));
                        string note = string.Format(Tr("Пакетов к очистке: {0}. Windows рекомендует очистку: {1}. Цифра — верхняя граница: обычно освобождается заметно меньше."),
                            a.ReclaimablePackages?.ToString(CultureInfo.CurrentCulture) ?? Tr("нет данных"),
                            a.CleanupRecommended switch { true => Tr("да"), false => Tr("нет"), _ => Tr("нет данных") });
                        return new CleanupMeasurement(a.ReclaimableUpperBound, true, note);
                    }
                default:
                    return new CleanupMeasurement(null, false, null);
            }

            static CleanupMeasurement Exact(long bytes) => new(bytes, false, null);
        }

        /// <summary>Размер файлов в папке (рекурсивно, без перехода по ссылкам). minAge — только файлы старше.</summary>
        public static long DirectorySize(string path, TimeSpan? minAge, CancellationToken ct)
        {
            if (!Directory.Exists(path)) return 0;
            DateTime cutoff = minAge is { } age ? DateTime.UtcNow - age : DateTime.MaxValue;
            long total = 0;
            var dir = new DirectoryInfo(path);
            int n = 0;
            try
            {
                foreach (FileInfo f in dir.EnumerateFiles("*", RecursiveOptions()))
                {
                    if ((++n & 255) == 0) ct.ThrowIfCancellationRequested();
                    if (minAge != null && f.LastWriteTimeUtc > cutoff) continue;
                    total += f.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Папка закрыта или исчезла посреди подсчёта — берём то, что успели.
            }
            return total;
        }

        private static EnumerationOptions RecursiveOptions() => new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        private static long SafeLength(string file)
        {
            try { return new FileInfo(file).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        }

        private static IEnumerable<string> TempRoots()
        {
            var roots = new List<string> { Path.GetTempPath(), Path.Combine(WindowsDir, "Temp") };
            return roots
                .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(IsSafeTempRoot);
        }

        /// <summary>
        /// Защита от удаления «не того»: папка временных файлов должна называться Temp
        /// (или Tmp) и не быть корнем диска. Если переменная TEMP указывает, скажем,
        /// на «C:\» или на «Документы», клиент её не чистит.
        /// </summary>
        public static bool IsSafeTempRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string full = Path.TrimEndingDirectorySeparator(path);
            string? root = Path.GetPathRoot(full);
            if (root == null || string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
                return false;
            string name = Path.GetFileName(full);
            return name.Equals("Temp", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Tmp", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> DumpPaths()
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(WindowsDir, "Minidump");
            yield return Path.Combine(WindowsDir, "MEMORY.DMP");
            foreach (string sub in new[] { "ReportArchive", "ReportQueue", "Temp" })
                yield return Path.Combine(programData, "Microsoft", "Windows", "WER", sub);
            foreach (string sub in new[] { "ReportArchive", "ReportQueue" })
                yield return Path.Combine(localAppData, "Microsoft", "Windows", "WER", sub);
        }

        private static IEnumerable<string> DeliveryOptimizationPaths()
        {
            yield return Path.Combine(WindowsDir, "ServiceProfiles", "NetworkService", "AppData", "Local",
                "Microsoft", "Windows", "DeliveryOptimization", "Cache");
            yield return Path.Combine(WindowsDir, "SoftwareDistribution", "DeliveryOptimization");
        }

        // ── DISM: анализ хранилища компонентов ───────────────────────────────

        private static string DismExe => Path.Combine(SystemDir, "Dism.exe");
        private static string CleanMgrExe => Path.Combine(SystemDir, "cleanmgr.exe");

        private static async Task<ComponentStoreAnalysis?> AnalyzeComponentStoreAsync(CancellationToken ct)
        {
            if (!File.Exists(DismExe)) return null;
            // /English — вывод на английском при любом языке системы: разбор переведённого
            // вывода ломался бы на каждой локализации.
            var (code, output) = await RunToolAsync(DismExe,
                new[] { "/English", "/Online", "/Cleanup-Image", "/AnalyzeComponentStore" }, ct).ConfigureAwait(false);
            if (code != 0)
            {
                AppLogger.Write($"[Место на диске] DISM AnalyzeComponentStore: код {code}");
                return null;
            }
            return ParseComponentStoreAnalysis(output);
        }

        /// <summary>Разбор вывода DISM /English /AnalyzeComponentStore. Чистая функция — покрыта тестами.</summary>
        public static ComponentStoreAnalysis? ParseComponentStoreAnalysis(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return null;
            long? actual = null, backups = null, cache = null;
            int? packages = null;
            bool? recommended = null;
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                int colon = line.IndexOf(" : ", StringComparison.Ordinal);
                if (colon < 0) continue;
                string label = line[..colon].Trim();
                string value = line[(colon + 3)..].Trim();
                switch (label)
                {
                    case "Actual Size of Component Store": actual = ParseSize(value); break;
                    case "Backups and Disabled Features": backups = ParseSize(value); break;
                    case "Cache and Temporary Data": cache = ParseSize(value); break;
                    case "Number of Reclaimable Packages":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) packages = n;
                        break;
                    case "Component Store Cleanup Recommended":
                        if (value.Equals("Yes", StringComparison.OrdinalIgnoreCase)) recommended = true;
                        else if (value.Equals("No", StringComparison.OrdinalIgnoreCase)) recommended = false;
                        break;
                }
            }
            if (actual == null && backups == null && cache == null && packages == null && recommended == null)
                return null;
            return new ComponentStoreAnalysis(actual, backups, cache, packages, recommended);
        }

        private static readonly Regex SizeRegex = new(
            @"^(?<num>\d+(?:[.,]\d+)?)\s*(?<unit>bytes|KB|MB|GB|TB)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>«8.45 GB», «299,34 MB», «0 bytes» → байты. null — не распознано.</summary>
        public static long? ParseSize(string text)
        {
            Match m = SizeRegex.Match(text.Trim());
            if (!m.Success) return null;
            if (!double.TryParse(m.Groups["num"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                return null;
            double mult = m.Groups["unit"].Value.ToUpperInvariant() switch
            {
                "KB" => 1024d,
                "MB" => 1024d * 1024,
                "GB" => 1024d * 1024 * 1024,
                "TB" => 1024d * 1024 * 1024 * 1024,
                _ => 1d,
            };
            return (long)Math.Round(num * mult);
        }

        // ── Очистка ──────────────────────────────────────────────────────────

        /// <summary>
        /// Удалить выбранные категории. Порядок: «Очистка диска» одним запуском на все
        /// её категории, затем файлы, корзина и в самом конце DISM — он самый долгий.
        /// Отмены нет намеренно: прерывать DISM или cleanmgr посреди работы опаснее, чем дождаться.
        /// </summary>
        public static async Task CleanAsync(IReadOnlyList<CleanupCategory> selected, IProgress<string>? progress)
        {
            var handlers = selected.Where(c => c.Method == CleanupMethod.CleanMgr)
                .SelectMany(c => c.CleanMgrHandlers).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (handlers.Count > 0)
            {
                progress?.Report(Tr("Работает «Очистка диска» Windows…"));
                await RunCleanMgrAsync(handlers).ConfigureAwait(false);
            }

            foreach (CleanupCategory c in selected)
            {
                switch (c.Method)
                {
                    case CleanupMethod.DirectFiles:
                        progress?.Report(Tr("Удаление временных файлов…"));
                        await Task.Run(() =>
                        {
                            foreach (string root in TempRoots())
                                DeleteFilesUnder(root, TempMinAge);
                        }).ConfigureAwait(false);
                        break;
                    case CleanupMethod.UpdateCache:
                        progress?.Report(Tr("Удаление кэша обновлений…"));
                        await Task.Run(ClearUpdateCache).ConfigureAwait(false);
                        break;
                    case CleanupMethod.RecycleBin:
                        progress?.Report(Tr("Очистка корзины…"));
                        EmptyRecycleBin(SystemDriveRoot);
                        break;
                }
            }

            if (selected.Any(c => c.Method == CleanupMethod.ComponentStore))
            {
                progress?.Report(Tr("Очистка хранилища компонентов (DISM) — это может занять до получаса…"));
                var (code, _) = await RunToolAsync(DismExe,
                    new[] { "/English", "/Online", "/Cleanup-Image", "/StartComponentCleanup" }, CancellationToken.None)
                    .ConfigureAwait(false);
                AppLogger.Write($"[Место на диске] DISM StartComponentCleanup: код {code}");
            }
        }

        private static async Task RunCleanMgrAsync(IReadOnlyList<string> handlers)
        {
            if (!File.Exists(CleanMgrExe))
            {
                AppLogger.Write("[Место на диске] cleanmgr.exe не найден — категории «Очистки диска» пропущены");
                return;
            }

            RemoveOwnStateFlags(); // хвосты прошлого запуска, если клиент тогда закрыли посреди работы
            int set = 0;
            try
            {
                foreach (string handler in handlers)
                {
                    using RegistryKey? key = Registry.LocalMachine.OpenSubKey(VolumeCachesKey + "\\" + handler, writable: true);
                    if (key == null) continue; // в этой версии Windows такого обработчика нет
                    key.SetValue(StateFlagsValue, 2, RegistryValueKind.DWord);
                    set++;
                }
                if (set == 0)
                {
                    AppLogger.Write("[Место на диске] Ни одного обработчика «Очистки диска» не найдено");
                    return;
                }

                AppLogger.Write($"[Место на диске] cleanmgr /sagerun:{SageSet}, обработчиков: {set}");
                var psi = new ProcessStartInfo(CleanMgrExe) { UseShellExecute = false };
                psi.ArgumentList.Add($"/sagerun:{SageSet}");
                using Process? p = Process.Start(psi);
                if (p != null)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromHours(2));
                    try { await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { AppLogger.Write("[Место на диске] cleanmgr не закончил за 2 часа"); }
                }
            }
            finally
            {
                // Свой набор после работы убираем всегда — даже при ошибке.
                RemoveOwnStateFlags();
            }
        }

        /// <summary>Убрать значение StateFlags6505 у всех обработчиков «Очистки диска».</summary>
        public static void RemoveOwnStateFlags()
        {
            try
            {
                using RegistryKey? root = Registry.LocalMachine.OpenSubKey(VolumeCachesKey);
                if (root == null) return;
                foreach (string name in root.GetSubKeyNames())
                {
                    using RegistryKey? key = root.OpenSubKey(name, writable: true);
                    if (key?.GetValue(StateFlagsValue) != null) key.DeleteValue(StateFlagsValue, throwOnMissingValue: false);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                AppLogger.Write($"[Место на диске] Не удалось убрать {StateFlagsValue}: {ex.Message}");
            }
        }

        private static void ClearUpdateCache()
        {
            string download = Path.Combine(WindowsDir, "SoftwareDistribution", "Download");
            // Служба обновлений держит файлы этой папки. Останавливаем её (и BITS, который
            // докачивает обновления) на время удаления и запускаем обратно — только те,
            // что работали до нас.
            var restart = new List<string>();
            foreach (string name in new[] { "wuauserv", "bits" })
            {
                try
                {
                    using var sc = new ServiceController(name);
                    if (sc.Status == ServiceControllerStatus.Stopped) continue;
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
                    restart.Add(name);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
                {
                    AppLogger.Write($"[Место на диске] Служба {name} не остановлена: {ex.Message}");
                }
            }
            try
            {
                DeleteFilesUnder(download, null);
            }
            finally
            {
                foreach (string name in restart)
                {
                    try
                    {
                        using var sc = new ServiceController(name);
                        if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        AppLogger.Write($"[Место на диске] Служба {name} не запущена обратно: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Удалить файлы внутри папки (саму папку не трогаем). По ссылкам и точкам
        /// соединения не переходим. Занятые файлы пропускаются. Пустые подпапки убираются.
        /// </summary>
        private static void DeleteFilesUnder(string root, TimeSpan? minAge)
        {
            if (!Directory.Exists(root)) return;
            DateTime cutoff = minAge is { } age ? DateTime.UtcNow - age : DateTime.MaxValue;
            int deleted = 0, skipped = 0;
            foreach (FileInfo f in new DirectoryInfo(root).EnumerateFiles("*", RecursiveOptions()))
            {
                try
                {
                    if (minAge != null && f.LastWriteTimeUtc > cutoff) continue;
                    if (f.IsReadOnly) f.IsReadOnly = false;
                    f.Delete();
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                }
            }
            // Пустые подпапки — от самых глубоких к верхним; непустые и занятые остаются.
            try
            {
                foreach (string dir in Directory.EnumerateDirectories(root, "*", RecursiveOptions())
                             .OrderByDescending(d => d.Length))
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            AppLogger.Write($"[Место на диске] {Path.GetFileName(root)}: удалено файлов {deleted}, пропущено занятых {skipped}");
        }

        // ── Вспомогательное ──────────────────────────────────────────────────

        private static async Task<(int Code, string Output)> RunToolAsync(string exe, IEnumerable<string> args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using Process p = Process.Start(psi) ?? throw new InvalidOperationException("Процесс не запустился");
            Task<string> stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                throw;
            }
            string output = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            return (p.ExitCode, output);
        }

        /// <summary>Свободное место на системном диске, байты. null — не удалось прочитать.</summary>
        public static long? GetSystemDriveFree()
        {
            try { return new DriveInfo(SystemDriveRoot).AvailableFreeSpace; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        }

        /// <summary>Размер для показа: «512 МБ», «3,4 ГБ».</summary>
        public static string FormatSize(long bytes)
        {
            const double gb = 1024d * 1024 * 1024;
            if (bytes >= gb) return string.Format(CultureInfo.CurrentCulture, "{0:0.0} ГБ", bytes / gb);
            double mb = bytes / 1024d / 1024d;
            return mb >= 1 || bytes == 0
                ? string.Format(CultureInfo.CurrentCulture, "{0:0} МБ", mb)
                : "< 1 МБ";
        }

        private static long? QueryRecycleBin(string root)
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            int hr = SHQueryRecycleBin(root, ref info);
            return hr == 0 ? info.i64Size : null;
        }

        private static void EmptyRecycleBin(string root)
        {
            const uint flags = 0x1 | 0x2 | 0x4; // без подтверждения, без окна прогресса, без звука
            int hr = SHEmptyRecycleBin(IntPtr.Zero, root, flags);
            // Пустая корзина возвращает E_UNEXPECTED — это не ошибка.
            if (hr != 0 && hr != unchecked((int)0x8000FFFF))
                AppLogger.Write($"[Место на диске] Корзина: SHEmptyRecycleBin вернул 0x{hr:X8}");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);
    }
}
