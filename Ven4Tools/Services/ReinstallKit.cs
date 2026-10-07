using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    /// <summary>Запуск системных утилит набора (pnputil, netsh); вынесен интерфейсом ради тестов.</summary>
    public interface IKitCommandRunner
    {
        Task<(int ExitCode, string Output)> RunAsync(
            string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
    }

    /// <summary>Установленная программа для набора «Перед переустановкой».</summary>
    /// <param name="Source">Источник по данным winget («winget», «msstore») или пусто — запись только из «Программ и компонентов».</param>
    public sealed record KitInstalledProgram(string Name, string Id, string Source);

    /// <summary>
    /// «Перед переустановкой»: набор на флешку, с которым после чистой установки Windows
    /// не приходится искать драйвер сетевой карты, вспоминать пароли Wi-Fi и список
    /// программ.
    ///
    /// В набор складываются сторонние драйверы этого компьютера, профили Wi-Fi, файл
    /// ответа с программами каталога, которые здесь стоят, и список того, что само не
    /// вернётся. Рядом кладётся копия клиента и <c>restore.cmd</c>: на свежей системе
    /// он возвращает драйверы и Wi-Fi и запускает установку программ — в таком порядке,
    /// потому что без сетевого драйвера всё остальное недоступно.
    /// </summary>
    public static class ReinstallKitBuilder
    {
        public const string AnswerFileName = "ven4tools-restore.json";
        public const string LauncherFileName = "restore.cmd";
        public const string ReadmeFileName = "ПРОЧТИ.txt";
        public const string ManualListFileName = "Поставить вручную.txt";
        public const string WingetExportFileName = "winget-export.json";
        public const string DriversFolderName = "Drivers";
        public const string WifiFolderName = "WiFi";

        public sealed record Options(string KitRoot, bool Drivers, bool Wifi, bool Apps, bool CopyClient = true);

        public sealed class Result
        {
            /// <summary>Сколько пакетов драйверов выгружено; null — драйверы не запрашивались.</summary>
            public int? Drivers { get; set; }
            public long DriversBytes { get; set; }
            /// <summary>Сколько профилей Wi-Fi выгружено; null — не запрашивались.</summary>
            public int? WifiProfiles { get; set; }
            /// <summary>Программы каталога, которые вернёт файл ответа; null — не запрашивались.</summary>
            public int? CatalogApps { get; set; }
            /// <summary>Программы, которых нет в каталоге, но которые знает winget.</summary>
            public int WingetOnly { get; set; }
            /// <summary>Программы, которые придётся ставить вручную.</summary>
            public int Manual { get; set; }
            /// <summary>Что не получилось — человеческим языком; пусто, если всё собрано.</summary>
            public List<string> Warnings { get; } = new();
        }

        /// <param name="catalogApps">Программы каталога, установленные на этом компьютере: идентификатор каталога, название, идентификатор winget.</param>
        /// <param name="installed">Всё, что видит <c>winget list</c>.</param>
        /// <param name="clientDirectory">Папка работающего клиента — копируется в набор.</param>
        public static async Task<Result> BuildAsync(
            Options options,
            IReadOnlyList<(string Id, string Name, string WingetId)> catalogApps,
            IReadOnlyList<KitInstalledProgram> installed,
            string clientDirectory,
            IKitCommandRunner runner,
            IProgress<string>? progress = null,
            CancellationToken ct = default,
            Func<string, Task<bool>>? exportWinget = null)
        {
            if (!options.Drivers && !options.Wifi && !options.Apps)
                throw new InvalidOperationException("Не выбрано ничего, что положить в набор.");

            string root = Path.GetFullPath(options.KitRoot);
            Directory.CreateDirectory(root);
            var result = new Result();

            if (options.Drivers)
            {
                progress?.Report("Выгрузка драйверов…");
                await ExportDriversAsync(root, runner, result, ct);
            }

            if (options.Wifi)
            {
                progress?.Report("Выгрузка профилей Wi-Fi…");
                await ExportWifiAsync(root, runner, result, ct);
            }

            var catalogIds = new List<string>();
            if (options.Apps)
            {
                progress?.Report("Список программ…");
                catalogIds = catalogApps.Select(a => a.Id).ToList();
                result.CatalogApps = catalogIds.Count;

                var (wingetOnly, manual) = SplitOutsideCatalog(catalogApps, installed);
                result.WingetOnly = wingetOnly.Count;
                result.Manual = manual.Count;

                // Полный список winget в его собственном формате: его принимает и
                // «Импорт» на вкладке «Установленные», и сам winget import.
                bool exported = exportWinget != null && await exportWinget(Path.Combine(root, WingetExportFileName));
                File.WriteAllText(Path.Combine(root, ManualListFileName),
                    BuildManualList(catalogApps.Select(a => a.Name).ToList(), wingetOnly, manual, exported),
                    new UTF8Encoding(true));
            }

            File.WriteAllText(Path.Combine(root, AnswerFileName),
                BuildAnswerFile(catalogIds, result.Drivers > 0, result.WifiProfiles > 0), new UTF8Encoding(false));
            // Только ASCII: cmd.exe читает пакетный файл в OEM-кодировке.
            File.WriteAllText(Path.Combine(root, LauncherFileName), LauncherScript, Encoding.ASCII);
            File.WriteAllText(Path.Combine(root, ReadmeFileName), BuildReadme(result), new UTF8Encoding(true));

            if (options.CopyClient)
            {
                progress?.Report("Копирование клиента…");
                await Task.Run(() => OfflineKitBuilder.CopyClient(
                    clientDirectory, Path.Combine(root, OfflineKitBuilder.ClientFolderName), ct), ct);
            }

            return result;
        }

        private static async Task ExportDriversAsync(string root, IKitCommandRunner runner, Result result, CancellationToken ct)
        {
            string folder = Path.Combine(root, DriversFolderName);
            ClearPreviousExport(folder);
            Directory.CreateDirectory(folder);

            // Драйверы одной видеокарты — это гигабайты; на заполненной флешке pnputil
            // выгрузил бы часть и остановился с ничего не объясняющим кодом.
            if (FreeSpaceOf(folder) is { } free && free < MinFreeBytesForDrivers)
            {
                result.Drivers = 0;
                result.Warnings.Add(
                    $"Драйверы не выгружены: на диске свободно {free / 1024 / 1024} МБ, а драйверы занимают до нескольких гигабайт.");
                return;
            }

            var (code, output) = await runner.RunAsync(
                KitCommands.PnpUtil, new[] { "/export-driver", "*", folder }, TimeSpan.FromMinutes(20), ct);

            // Считаем по факту: pnputil возвращает ненулевой код и тогда, когда часть
            // пакетов выгрузилась, а один не дался.
            // Обход идёт не в потоке окна: на медленной флешке это гигабайты файлов.
            (result.Drivers, result.DriversBytes) = await Task.Run(() =>
            {
                if (!Directory.Exists(folder)) return (0, 0L);
                int packages = Directory.GetFiles(folder, "*.inf", SearchOption.AllDirectories)
                    .Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                long bytes = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                return (packages, bytes);
            }, ct);

            if (result.Drivers == 0)
                result.Warnings.Add($"Драйверы не выгружены (pnputil, код {code}). {LastLine(output)}".TrimEnd());
            else if (code != 0)
                result.Warnings.Add($"Часть драйверов могла не выгрузиться (pnputil, код {code}).");
        }

        private const long MinFreeBytesForDrivers = 1024L * 1024 * 1024;

        /// <summary>Свободное место на диске папки; null — узнать не удалось (сетевой путь и т.п.).</summary>
        private static long? FreeSpaceOf(string folder)
        {
            try
            {
                string? driveRoot = Path.GetPathRoot(Path.GetFullPath(folder));
                return string.IsNullOrEmpty(driveRoot) ? null : new DriveInfo(driveRoot).AvailableFreeSpace;
            }
            catch { return null; }
        }

        private static async Task ExportWifiAsync(string root, IKitCommandRunner runner, Result result, CancellationToken ct)
        {
            string folder = Path.Combine(root, WifiFolderName);
            ClearPreviousExport(folder);
            Directory.CreateDirectory(folder);
            // key=clear: без него профиль переносится без пароля и на новой системе бесполезен.
            var (code, _) = await runner.RunAsync(
                KitCommands.Netsh, new[] { "wlan", "export", "profile", "key=clear", "folder=" + folder },
                TimeSpan.FromMinutes(2), ct);

            result.WifiProfiles = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.xml").Length : 0;
            if (result.WifiProfiles == 0)
            {
                // Код 0 и ни одного файла — профилей действительно нет (компьютер без
                // Wi-Fi). Утилита не запустилась или не уложилась во время (отрицательный
                // код) — это уже не «профилей нет», и молчать об этом нельзя.
                if (code < 0)
                    result.Warnings.Add("Профили Wi-Fi не выгружены: netsh не ответил. Проверьте, что служба беспроводной сети работает, и соберите набор ещё раз.");
                try { Directory.Delete(folder); } catch { /* папка непуста или занята — оставляем */ }
            }
        }

        /// <summary>
        /// Убирает выгруженное прошлой сборкой в ту же папку. Иначе в набор попали бы
        /// драйверы и сети, которых на компьютере уже нет, а счёт выгруженного был бы завышен.
        /// Удаляются только файлы тех видов, которые кладёт сама выгрузка.
        /// </summary>
        private static void ClearPreviousExport(string folder)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Набор] Прошлая выгрузка не убрана ({Path.GetFileName(folder)}): {ex.Message}");
            }
        }

        /// <summary>
        /// Делит установленное вне каталога на то, что вернёт winget, и то, что придётся
        /// ставить вручную. Записи Microsoft Store в ручной список не попадают: магазин
        /// возвращает их сам из «Библиотеки».
        /// </summary>
        internal static (List<KitInstalledProgram> WingetOnly, List<KitInstalledProgram> Manual) SplitOutsideCatalog(
            IReadOnlyList<(string Id, string Name, string WingetId)> catalogApps,
            IReadOnlyList<KitInstalledProgram> installed)
        {
            var inCatalog = new HashSet<string>(
                catalogApps.Select(a => a.WingetId).Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.OrdinalIgnoreCase);

            var wingetOnly = new List<KitInstalledProgram>();
            var manual = new List<KitInstalledProgram>();
            foreach (var program in installed)
            {
                if (string.IsNullOrWhiteSpace(program.Name) || inCatalog.Contains(program.Id)) continue;
                if (program.Source.Equals("winget", StringComparison.OrdinalIgnoreCase)) wingetOnly.Add(program);
                else if (string.IsNullOrWhiteSpace(program.Source) && !IsSystemComponent(program)) manual.Add(program);
            }
            return (wingetOnly.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
                    manual.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        }

        // Пакеты Appx и компоненты Windows winget показывает с идентификатором вида
        // «MSIX\…»: это не программы, которые человек ставил и будет искать заново.
        private static bool IsSystemComponent(KitInstalledProgram program) =>
            program.Id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase);

        internal static string BuildManualList(
            IReadOnlyList<string> catalogNames,
            IReadOnlyList<KitInstalledProgram> wingetOnly,
            IReadOnlyList<KitInstalledProgram> manual,
            bool wingetExported = false)
        {
            var text = new StringBuilder();
            text.AppendLine("Программы этого компьютера");
            text.AppendLine("==========================");
            text.AppendLine();
            text.AppendLine($"Вернутся сами — их поставит {LauncherFileName}: {catalogNames.Count}");
            foreach (string name in catalogNames.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
                text.AppendLine("  " + name);
            text.AppendLine();
            text.AppendLine($"Есть в winget, но нет в каталоге Ven4Tools: {wingetOnly.Count}");
            if (wingetExported)
                text.AppendLine($"(все разом: вкладка «Установленные» → «Импорт» → файл {WingetExportFileName})");
            foreach (var program in wingetOnly)
                text.AppendLine($"  {program.Name}  —  winget install --id {program.Id}");
            text.AppendLine();
            text.AppendLine($"Поставить вручную — автоматически не вернутся: {manual.Count}");
            foreach (var program in manual)
                text.AppendLine("  " + program.Name);
            text.AppendLine();
            text.AppendLine("Что ещё проверить до переустановки");
            text.AppendLine("  - лицензионные ключи и файлы лицензий платных программ;");
            text.AppendLine("  - сертификаты электронной подписи и программы банков;");
            text.AppendLine("  - программы для принтера, мыши, клавиатуры и другой периферии;");
            text.AppendLine("  - игры из Steam, Epic и других магазинов — их возвращает сам магазин;");
            text.AppendLine("  - закладки и пароли браузера, если не включена синхронизация.");
            return text.ToString();
        }

        /// <summary>
        /// Файл ответа набора. Пути к драйверам и Wi-Fi — относительные: набор работает
        /// с любой буквой диска. Тихий режим выключен: восстановление запускает человек.
        /// </summary>
        public static string BuildAnswerFile(IReadOnlyList<string> appIds, bool drivers, bool wifi) =>
            JsonSerializer.Serialize(
                new
                {
                    apps = appIds,
                    silent = false,
                    restorePoint = false,
                    allowPackageManagers = true,
                    drivers = drivers ? DriversFolderName : null,
                    wifi = wifi ? WifiFolderName : null
                },
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });

        public const string LauncherScript =
            "@echo off\r\n" +
            "rem Ven4Tools: restores drivers and Wi-Fi profiles, then installs the programs listed next to this file.\r\n" +
            "start \"\" \"%~dp0" + OfflineKitBuilder.ClientFolderName + "\\Ven4Tools.exe\" --answer-file \"%~dp0" + AnswerFileName + "\"\r\n";

        private static string BuildReadme(Result result)
        {
            var text = new StringBuilder();
            text.AppendLine("Набор Ven4Tools «Перед переустановкой»");
            text.AppendLine("======================================");
            text.AppendLine();
            text.AppendLine("В наборе:");
            if (result.Drivers is { } drivers)
                text.AppendLine($"  - драйверы этого компьютера: {drivers} (папка {DriversFolderName});");
            if (result.WifiProfiles is { } wifi && wifi > 0)
                text.AppendLine($"  - профили Wi-Fi: {wifi} (папка {WifiFolderName});");
            if (result.CatalogApps is { } apps)
                text.AppendLine($"  - программы, которые вернутся сами: {apps}; остальные — в файле «{ManualListFileName}».");
            text.AppendLine();
            text.AppendLine("После установки Windows:");
            text.AppendLine($"  1. Откройте {LauncherFileName} с этой флешки и разрешите запуск от имени администратора.");
            text.AppendLine("  2. Ven4Tools вернёт драйверы и Wi-Fi, затем начнёт установку программ.");
            text.AppendLine("  3. Остальное поставьте по списку вручную.");
            text.AppendLine();
            if (result.WifiProfiles > 0)
            {
                text.AppendLine("ВНИМАНИЕ: пароли Wi-Fi лежат в папке WiFi открытым текстом — иначе их не перенести.");
                text.AppendLine("Не оставляйте флешку без присмотра и удалите папку, когда она больше не нужна.");
                text.AppendLine();
            }
            text.AppendLine("Драйверы взяты с этого компьютера и подходят только к нему. Панели управления");
            text.AppendLine("видеокарты и звука в набор не входят — их ставит установщик производителя.");
            return text.ToString();
        }

        private static string LastLine(string output) =>
            output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
    }

    /// <summary>
    /// Возврат драйверов и профилей Wi-Fi из набора «Перед переустановкой» — на свежей
    /// системе, перед установкой программ.
    /// </summary>
    public static class ReinstallKitRestorer
    {
        public sealed record Outcome(int? DriverPackages, bool DriversRebootNeeded, int? WifiAdded, int WifiFailed, IReadOnlyList<string> Log)
        {
            /// <summary>pnputil не запустился или вернул код ошибки.</summary>
            public bool DriversFailed { get; init; }

            /// <summary>Что-то из набора не вернулось — тихий режим не должен сообщать об успехе.</summary>
            public bool Failed => DriversFailed || WifiFailed > 0;
        }

        /// <summary>Сколько пакетов драйверов и профилей Wi-Fi лежит в наборе.</summary>
        public static (int Drivers, int Wifi) Describe(string? driversPath, string? wifiPath)
        {
            int drivers = driversPath != null && Directory.Exists(driversPath)
                ? Directory.GetFiles(driversPath, "*.inf", SearchOption.AllDirectories)
                    .Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                : 0;
            int wifi = wifiPath != null && Directory.Exists(wifiPath)
                ? Directory.GetFiles(wifiPath, "*.xml").Length
                : 0;
            return (drivers, wifi);
        }

        public static async Task<Outcome> RestoreAsync(
            string? driversPath, string? wifiPath, IKitCommandRunner runner,
            IProgress<string>? progress = null, CancellationToken ct = default)
        {
            var log = new List<string>();
            var (driverCount, wifiCount) = Describe(driversPath, wifiPath);
            int? driverPackages = null;
            bool reboot = false;
            int? wifiAdded = null;
            int wifiFailed = 0;
            bool driversFailed = false;

            if (driverCount > 0)
            {
                progress?.Report($"Установка драйверов: {driverCount}…");
                var (code, _) = await runner.RunAsync(
                    KitCommands.PnpUtil,
                    new[] { "/add-driver", Path.Combine(driversPath!, "*.inf"), "/subdirs", "/install" },
                    TimeSpan.FromMinutes(30), ct);
                driverPackages = driverCount;
                // 3010 — драйверы поставлены, нужна перезагрузка; 259 — все уже стояли.
                reboot = code == 3010;
                // pnputil сообщает о перезагрузке не только кодом 3010: проверено вживую —
                // при коде 259 он тоже может просить перезапуск текстом. Поэтому совет
                // перезагрузиться даётся всегда, а не только по коду.
                driversFailed = code is not (0 or 3010 or 259);
                log.Add(!driversFailed
                    ? $"Драйверы возвращены: пакетов в наборе {driverCount}. " +
                      (reboot ? "Нужна перезагрузка." : "Если какое-то устройство не заработало, перезагрузите компьютер.")
                    : code < 0
                        ? "Драйверы не возвращены: pnputil не запустился или не уложился в отведённое время"
                        : $"Драйверы возвращены не полностью (pnputil, код {code}): часть устройств могла уже иметь более новый драйвер");
            }

            if (wifiCount > 0)
            {
                progress?.Report($"Профили Wi-Fi: {wifiCount}…");
                wifiAdded = 0;
                foreach (string file in Directory.GetFiles(wifiPath!, "*.xml"))
                {
                    ct.ThrowIfCancellationRequested();
                    var (code, _) = await runner.RunAsync(
                        KitCommands.Netsh, new[] { "wlan", "add", "profile", "filename=" + file, "user=all" },
                        TimeSpan.FromSeconds(30), ct);
                    if (code == 0) wifiAdded++; else wifiFailed++;
                }
                log.Add($"Профили Wi-Fi возвращены: {wifiAdded}" + (wifiFailed > 0 ? $", не добавлено: {wifiFailed}" : ""));
            }

            return new Outcome(driverPackages, reboot, wifiAdded, wifiFailed, log) { DriversFailed = driversFailed };
        }
    }

    /// <summary>Системные утилиты набора — только из System32, а не по PATH: клиент работает с правами администратора.</summary>
    public static class KitCommands
    {
        public static string PnpUtil { get; } = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
        public static string Netsh => TrustedExecutablePaths.NetshExe;
    }

    /// <summary>Настоящий запуск утилит: без окна, с ограничением по времени.</summary>
    public sealed class KitCommandRunner : IKitCommandRunner
    {
        public static KitCommandRunner Default { get; } = new();

        public async Task<(int ExitCode, string Output)> RunAsync(
            string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo(fileName)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                foreach (string argument in arguments) psi.ArgumentList.Add(argument);

                using var process = Process.Start(psi);
                if (process == null) return (-1, "");

                var outTask = process.StandardOutput.ReadToEndAsync();
                var errTask = process.StandardError.ReadToEndAsync();

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout);
                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    ct.ThrowIfCancellationRequested();
                    AppLogger.Write($"[Набор] {Path.GetFileName(fileName)}: превышено время ожидания");
                    return (-1, "");
                }

                await Task.WhenAll(outTask, errTask);
                return (process.ExitCode, outTask.Result);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Write($"[Набор] {Path.GetFileName(fileName)}: {ex.Message}");
                return (-1, "");
            }
        }
    }
}
