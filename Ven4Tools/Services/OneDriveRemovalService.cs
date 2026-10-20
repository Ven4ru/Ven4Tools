using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Ven4Tools.Services
{
    /// <summary>Итог удаления или возврата OneDrive: успех и пояснение для журнала/пользователя.</summary>
    public sealed record OneDriveResult(bool Ok, string? Message);

    /// <summary>
    /// Удаление OneDrive из «Очистки» и его возврат.
    ///
    /// Главная опасность — перенесённые в OneDrive папки (резервное копирование
    /// «Рабочего стола», «Документов», «Изображений»). Если такая папка указывает внутрь
    /// OneDrive, после удаления клиента синхронизации пользователь видит пустой рабочий
    /// стол, а файлы остаются только в облаке и в «осиротевшей» папке. Поэтому в этом
    /// случае удаление не выполняется вовсе — с объяснением, что сделать сначала.
    ///
    /// Удаляет штатный установщик OneDriveSetup.exe /uninstall. Установка в профиль
    /// пользователя (обычный случай) удаляется без прав администратора — так же, как
    /// пользователь сделал бы это сам; установка для всех (Program Files) — с правами.
    /// Возврат — установка пакета Microsoft.OneDrive через winget, тоже без прав.
    /// </summary>
    public static class OneDriveRemovalService
    {
        public const string WingetId = "Microsoft.OneDrive";

        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OneDriveSetup.exe";
        private const string ShellFoldersKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";

        private static readonly TimeSpan UninstallLimit = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ReinstallLimit = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Папки профиля, которые OneDrive умеет забирать к себе («Резервное копирование
        /// папок»): значение в User Shell Folders → имя для пользователя.
        /// GUID-значения — дубли «Документов» и «Изображений» в новых сборках Windows.
        /// </summary>
        internal static readonly IReadOnlyList<(string ValueName, string DisplayName)> KnownFolders = new[]
        {
            ("Desktop", "Рабочий стол"),
            ("Personal", "Документы"),
            ("{F42EE2D3-909F-4907-8871-4C22FC0BF756}", "Документы"),
            ("My Pictures", "Изображения"),
            ("{0DDD015D-B06C-45D5-8C4C-F59713854639}", "Изображения"),
            ("My Music", "Музыка"),
            ("My Video", "Видео"),
        };

        // ── Чистая логика (проверяется юнит-тестами) ─────────────────────────────

        /// <summary>
        /// Какие из папок профиля лежат внутри OneDrive. Папка считается перенесённой,
        /// если её путь начинается с одного из корней OneDrive или любой её каталог
        /// называется «OneDrive…» («OneDrive», «OneDrive - Компания», «OneDrive - Personal»).
        /// </summary>
        /// <param name="folders">Имя папки для пользователя и её путь (уже с раскрытыми переменными).</param>
        /// <param name="oneDriveRoots">Корни OneDrive: переменные OneDrive*, папки учётных записей.</param>
        /// <returns>Имена перенесённых папок без повторов, в исходном порядке.</returns>
        internal static IReadOnlyList<string> FindFoldersInOneDrive(
            IEnumerable<(string DisplayName, string? Path)> folders,
            IEnumerable<string?> oneDriveRoots)
        {
            var roots = oneDriveRoots
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r!.Trim().TrimEnd('\\', '/'))
                .Where(r => r.Length > 3)
                .ToList();

            var result = new List<string>();
            foreach (var (name, rawPath) in folders)
            {
                if (string.IsNullOrWhiteSpace(rawPath)) continue;
                string path = rawPath.Trim().TrimEnd('\\', '/');

                bool underRoot = roots.Any(root =>
                    path.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));
                bool namedSegment = path
                    .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
                    .Any(segment => segment.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase));

                if ((underRoot || namedSegment) && !result.Contains(name))
                    result.Add(name);
            }
            return result;
        }

        /// <summary>
        /// Путь к OneDriveSetup.exe из строки удаления (кавычки и ключи отбрасываются:
        /// аргументы из реестра не используются никогда). null — строка не про OneDriveSetup.exe.
        /// </summary>
        internal static string? ParseSetupPath(string? uninstallString)
        {
            if (string.IsNullOrWhiteSpace(uninstallString)) return null;
            var match = Regex.Match(uninstallString, @"^\s*""?(?<path>[^""]*?OneDriveSetup\.exe)""?",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? match.Groups["path"].Value.Trim() : null;
        }

        /// <summary>
        /// Можно ли запускать этот OneDriveSetup.exe и как: из профиля пользователя — только
        /// без прав администратора (папка доступна пользователю на запись), из Program Files
        /// и системных папок — можно с правами. null — путь вне доверенных папок.
        /// </summary>
        internal static bool? IsPerUserSetup(string path, string localAppData, IEnumerable<string> machineDirs)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return null; }
            if (!string.Equals(Path.GetFileName(full), "OneDriveSetup.exe", StringComparison.OrdinalIgnoreCase))
                return null;

            static bool Under(string file, string dir) =>
                !string.IsNullOrEmpty(dir)
                && file.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

            if (Under(full, Path.Combine(localAppData, "Microsoft", "OneDrive"))) return true;
            if (machineDirs.Any(dir => Under(full, dir))) return false;
            return null;
        }

        // ── Система ──────────────────────────────────────────────────────────────

        /// <summary>Перенесённые в OneDrive папки текущего пользователя.</summary>
        public static IReadOnlyList<string> FindRedirectedFolders()
        {
            try
            {
                var folders = new List<(string, string?)>();
                using (var key = Registry.CurrentUser.OpenSubKey(ShellFoldersKey))
                {
                    foreach (var (valueName, displayName) in KnownFolders)
                    {
                        var raw = key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                        folders.Add((displayName, raw == null ? null : Environment.ExpandEnvironmentVariables(raw)));
                    }
                }
                return FindFoldersInOneDrive(folders, ReadOneDriveRoots());
            }
            catch (Exception ex)
            {
                // Не прочитали — не знаем; удалять вслепую нельзя, поэтому считаем,
                // что папки перенесены, и объясняем это пользователю.
                AppLogger.Write($"[OneDrive] Не прочитаны папки профиля: {ex.Message}");
                return new[] { "не удалось проверить папки профиля" };
            }
        }

        private static IEnumerable<string?> ReadOneDriveRoots()
        {
            yield return Environment.GetEnvironmentVariable("OneDrive");
            yield return Environment.GetEnvironmentVariable("OneDriveConsumer");
            yield return Environment.GetEnvironmentVariable("OneDriveCommercial");

            var accountFolders = new List<string>();
            try
            {
                using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
                foreach (var name in accounts?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var account = accounts!.OpenSubKey(name);
                    if (account?.GetValue("UserFolder") is string folder) accountFolders.Add(folder);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[OneDrive] Не прочитаны учётные записи OneDrive: {ex.Message}");
            }
            foreach (var folder in accountFolders) yield return folder;
        }

        /// <summary>Установлен ли OneDrive: есть запись удаления в профиле пользователя или для всех.</summary>
        public static bool IsInstalled() => FindUninstallString() != null;

        private static string? FindUninstallString()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
                    if (key?.GetValue("UninstallString") is string s && s.Length > 0) return s;

                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = root.OpenSubKey(UninstallKey);
                    if (key?.GetValue("UninstallString") is string s && s.Length > 0) return s;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[OneDrive] Не прочитана запись удаления: {ex.Message}");
            }
            return null;
        }

        private static IEnumerable<string> MachineDirs()
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        }

        /// <summary>
        /// Удаляет OneDrive. Ничего не делает, если папки профиля перенесены в OneDrive —
        /// тогда в <see cref="OneDriveResult.Message"/> объяснение для пользователя.
        /// </summary>
        public static async Task<OneDriveResult> UninstallAsync(CancellationToken ct = default)
        {
            var redirected = FindRedirectedFolders();
            if (redirected.Count > 0)
                return new(false, RedirectedMessage(redirected));

            string? uninstall = FindUninstallString();
            if (uninstall == null)
                return new(true, "OneDrive не установлен — удалять нечего.");

            string? setup = ParseSetupPath(uninstall);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            bool? perUser = setup == null ? null : IsPerUserSetup(setup, localAppData, MachineDirs());
            if (setup == null || perUser == null || !File.Exists(setup))
                return new(false, "Установщик OneDrive не найден в ожидаемой папке — удаление не выполнялось.");

            DateTime startedUtc = DateTime.UtcNow;
            if (perUser == true)
            {
                // Папка в профиле доступна пользователю на запись — с правами
                // администратора оттуда ничего не запускаем.
                if (!UnelevatedLauncher.TryStart(setup, "/uninstall"))
                    return new(false, "Не удалось запустить удаление OneDrive без прав администратора (Проводник не запущен?).");
            }
            else
            {
                var psi = new System.Diagnostics.ProcessStartInfo(setup, "/uninstall /allusers")
                {
                    UseShellExecute = false, CreateNoWindow = true
                };
                System.Diagnostics.Process.Start(psi)?.Dispose();
            }
            AppLogger.Write($"[OneDrive] Запущено удаление ({(perUser == true ? "профиль пользователя" : "для всех пользователей")})");

            await WaitForAsync("OneDriveSetup", startedUtc, UninstallLimit, ct);

            return IsInstalled()
                ? new(false, "OneDrive не удалился: запись об установке осталась. Подробности — в журнале.")
                : new(true, "OneDrive удалён.");
        }

        /// <summary>Возвращает OneDrive: установка пакета Microsoft.OneDrive через winget без прав администратора.</summary>
        public static async Task<OneDriveResult> ReinstallAsync(CancellationToken ct = default)
        {
            if (IsInstalled()) return new(true, "OneDrive уже установлен.");

            string? winget = TrustedExecutablePaths.ResolveWinget();
            if (winget == null)
                return new(false, "winget не найден — установите OneDrive вручную из Microsoft Store или с сайта Microsoft.");

            var args = WingetArgs.Modify("install", "--id", WingetId, "-e", "--source", "winget");
            DateTime startedUtc = DateTime.UtcNow;
            if (!UnelevatedLauncher.TryStart(winget, string.Join(" ", args.Select(AppUninstallService.QuoteArg))))
                return new(false, "Не удалось запустить установку OneDrive без прав администратора.");
            AppLogger.Write("[OneDrive] Запущена установка через winget");

            await WaitForAsync("winget", startedUtc, ReinstallLimit, ct);

            return IsInstalled()
                ? new(true, "OneDrive установлен заново.")
                : new(false, "OneDrive не установился. Подробности — в журнале winget.");
        }

        /// <summary>Объяснение, почему удалять OneDrive сейчас нельзя.</summary>
        public static string RedirectedMessage(IReadOnlyList<string> folders) =>
            "OneDrive не удалён: в него перенесены папки «" + string.Join("», «", folders) + "». " +
            "После удаления они остались бы пустыми, а файлы — только в облаке. " +
            "Сначала отключите резервное копирование папок в параметрах OneDrive " +
            "(«Синхронизация и резервное копирование» → «Управление резервным копированием») и дождитесь, пока файлы вернутся на диск.";

        private static async Task WaitForAsync(string processName, DateTime startedUtc, TimeSpan limit, CancellationToken ct)
        {
            try
            {
                await Task.Delay(3000, ct); // оболочке нужно время, чтобы процесс появился
                var deadline = DateTime.UtcNow + limit;
                while (DateTime.UtcNow < deadline
                       && AppUninstallService.CountStartedSince(processName, null, startedUtc, kill: false) > 0)
                    await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException)
            {
                AppLogger.Write($"[OneDrive] Ожидание {processName} прервано");
            }
        }
    }
}
