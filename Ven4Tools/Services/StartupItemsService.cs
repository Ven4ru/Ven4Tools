using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Ven4Tools.Shared;

namespace Ven4Tools.Services
{
    /// <summary>Откуда программа запускается вместе с Windows.</summary>
    public enum StartupSource
    {
        /// <summary>Значение в разделе реестра Run.</summary>
        RegistryRun,
        /// <summary>Значение в RunOnce — сработает один раз при следующем входе.</summary>
        RegistryRunOnce,
        /// <summary>Ярлык или файл в папке «Автозагрузка».</summary>
        StartupFolder,
        /// <summary>Задача планировщика с триггером «при входе» или «при запуске».</summary>
        ScheduledTask
    }

    /// <summary>Одна строка автозагрузки и всё, что нужно, чтобы её выключить и включить.</summary>
    public sealed record StartupEntry
    {
        public StartupSource Source { get; init; }
        public string Name { get; init; } = "";
        public string Command { get; init; } = "";
        /// <summary>Где лежит: раздел реестра, путь к файлу или путь задачи.</summary>
        public string Location { get; init; } = "";
        public bool Enabled { get; init; }
        /// <summary>Издатель по сертификату подписи или свойствам файла; «нет данных», если ни того ни другого.</summary>
        public string Publisher { get; init; } = "";
        /// <summary>Компонент Windows или программа Microsoft — по умолчанию не предлагается к отключению.</summary>
        public bool IsSystem { get; init; }
        /// <summary>Именно часть Windows (файл в папке Windows или задача \Microsoft\Windows\), а не программа Microsoft.</summary>
        public bool IsWindowsComponent { get; init; }
        /// <summary>Путь к исполняемому файлу, если его удалось выделить из команды.</summary>
        public string? ExecutablePath { get; init; }

        // Как выключать: значение в StartupApproved (как у Диспетчера задач) или задача.
        internal RegistryHive ApprovedHive { get; init; }
        internal string? ApprovedSubKey { get; init; }
        internal string? ApprovedValueName { get; init; }
        internal string? TaskPath { get; init; }

        /// <summary>RunOnce не выключается: он сработает один раз и сам исчезнет.</summary>
        public bool CanToggle => Source != StartupSource.RegistryRunOnce;
    }

    /// <summary>
    /// Автозагрузка целиком: разделы Run/RunOnce (пользователь, все пользователи,
    /// 32-разрядные программы), папки «Автозагрузка» и задачи планировщика, которые
    /// стартуют при входе или запуске Windows.
    ///
    /// Выключение — так же, как это делает Диспетчер задач: в разделе
    /// Explorer\StartupApproved пишется двоичная отметка «выключено», сама запись Run
    /// и ярлык остаются на месте. Задачи планировщика выключаются флагом Enabled.
    /// Ничего не удаляется, службы не трогаются.
    /// </summary>
    public static class StartupItemsService
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
        private const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        private const string RunOnce32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce";
        internal const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

        // Типы триггеров планировщика (TASK_TRIGGER_TYPE2): 8 — при запуске системы, 9 — при входе.
        private const int TriggerBoot = 8;
        private const int TriggerLogon = 9;
        // TASK_ACTION_EXEC — запуск программы.
        private const int ActionExec = 0;
        // TASK_ENUM_HIDDEN — показывать и скрытые задачи.
        private const int EnumHidden = 1;

        // ── Чистая логика (проверяется юнит-тестами) ─────────────────────────────

        /// <summary>
        /// Включена ли запись по значению StartupApproved. Нет значения — включена.
        /// Первый байт 02/06 — включена, 03/07 (младший бит установлен) — выключена.
        /// </summary>
        internal static bool IsApprovedEnabled(byte[]? data) =>
            data == null || data.Length == 0 || (data[0] & 1) == 0;

        /// <summary>
        /// Значение StartupApproved в том виде, в каком его пишет Диспетчер задач:
        /// 12 байт, первый 02 (включено) или 03 (выключено); у выключенной — время
        /// выключения (FILETIME) в байтах 4–11.
        /// </summary>
        internal static byte[] BuildApprovedValue(bool enabled, DateTime utcNow)
        {
            var data = new byte[12];
            data[0] = enabled ? (byte)0x02 : (byte)0x03;
            if (!enabled)
                BitConverter.GetBytes(utcNow.ToFileTimeUtc()).CopyTo(data, 4);
            return data;
        }

        /// <summary>Триггер «при запуске» или «при входе» — значит, задача стартует вместе с Windows.</summary>
        internal static bool IsStartupTrigger(int triggerType) => triggerType is TriggerBoot or TriggerLogon;

        /// <summary>
        /// Исполняемый файл из командной строки автозагрузки: путь в кавычках или самый
        /// длинный существующий префикс до пробела (пути с пробелами без кавычек — частый
        /// случай в Run). Голое имя («rundll32.exe», «powershell.exe») ищется в папках
        /// <paramref name="searchDirs"/> — системных и из PATH.
        /// </summary>
        internal static string? ExtractExecutable(string? command, Func<string, bool> fileExists, IReadOnlyList<string> searchDirs)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            string cmd = Environment.ExpandEnvironmentVariables(command).Trim();

            string? candidate;
            if (cmd.StartsWith('"'))
            {
                int end = cmd.IndexOf('"', 1);
                candidate = end > 1 ? cmd.Substring(1, end - 1) : cmd.Trim('"');
            }
            else
            {
                candidate = null;
                var parts = cmd.Split(' ');
                for (int i = parts.Length; i >= 1 && candidate == null; i--)
                {
                    string prefix = string.Join(' ', parts, 0, i);
                    if (fileExists(prefix)) candidate = prefix;
                    else if (fileExists(prefix + ".exe")) candidate = prefix + ".exe";
                }
                candidate ??= parts[0];
            }

            candidate = candidate.Trim();
            if (candidate.Length == 0) return null;
            if (!Path.IsPathRooted(candidate))
            {
                string bare = Path.HasExtension(candidate) ? candidate : candidate + ".exe";
                foreach (string dir in searchDirs)
                {
                    string inDir = Path.Combine(dir, bare);
                    if (fileExists(inDir)) return inDir;
                }
            }
            return candidate;
        }

        private static readonly HashSet<string> ScriptHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "powershell.exe", "pwsh.exe", "powershell_ise.exe", "cmd.exe", "wscript.exe", "cscript.exe",
            "mshta.exe", "rundll32.exe", "regsvr32.exe", "conhost.exe", "msiexec.exe", "schtasks.exe",
            "forfiles.exe", "bitsadmin.exe", "certutil.exe", "wmic.exe", "explorer.exe"
        };

        /// <summary>
        /// Системная программа-посредник, которая выполняет чужую команду или скрипт.
        /// По её подписи нельзя судить, чья запись в автозагрузке.
        /// </summary>
        internal static bool IsScriptHost(string? executablePath) =>
            !string.IsNullOrEmpty(executablePath) && ScriptHosts.Contains(Path.GetFileName(executablePath));

        /// <summary>Лежит ли файл внутри папки (без ложного совпадения «C:\Windows2»).</summary>
        internal static bool IsUnder(string? path, string dir)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(dir)) return false;
            return path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Значение поля O= (организация) из имени сертификата; null — поля нет.</summary>
        internal static string? OrganizationOf(string subject)
        {
            foreach (string part in SplitDistinguishedName(subject))
            {
                string trimmed = part.Trim();
                if (trimmed.StartsWith("O=", StringComparison.OrdinalIgnoreCase))
                    return trimmed[2..].Trim().Trim('"');
            }
            return null;
        }

        // Запятые внутри кавычек («O="Example, Inc."») — не разделители.
        private static IEnumerable<string> SplitDistinguishedName(string subject)
        {
            var current = new System.Text.StringBuilder();
            bool quoted = false;
            foreach (char c in subject)
            {
                if (c == '"') quoted = !quoted;
                if (c == ',' && !quoted)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                else current.Append(c);
            }
            if (current.Length > 0) yield return current.ToString();
        }

        // ── Чтение ───────────────────────────────────────────────────────────────

        /// <summary>Где искать программу, заданную в команде голым именем: системные папки и PATH машины.</summary>
        private static IReadOnlyList<string> SearchDirs()
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var dirs = new List<string>
            {
                system,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Path.Combine(system, "WindowsPowerShell", "v1.0")
            };
            string? path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
            foreach (string dir in (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    string expanded = Environment.ExpandEnvironmentVariables(dir);
                    if (Path.IsPathFullyQualified(expanded) && !dirs.Contains(expanded, StringComparer.OrdinalIgnoreCase))
                        dirs.Add(expanded);
                }
                catch { /* испорченный элемент PATH пропускается */ }
            }
            return dirs;
        }

        /// <summary>Всё, что стартует с Windows. Медленно (подписи файлов, планировщик) — только в фоне.</summary>
        public static Task<IReadOnlyList<StartupEntry>> ListAsync(CancellationToken ct = default) =>
            Task.Run<IReadOnlyList<StartupEntry>>(() =>
            {
                var raw = new List<StartupEntry>();
                ReadRunKey(raw, RegistryHive.CurrentUser, RunKey, "Run", StartupSource.RegistryRun);
                ReadRunKey(raw, RegistryHive.LocalMachine, RunKey, "Run", StartupSource.RegistryRun);
                ReadRunKey(raw, RegistryHive.LocalMachine, Run32Key, "Run32", StartupSource.RegistryRun);
                ReadRunKey(raw, RegistryHive.CurrentUser, RunOnceKey, null, StartupSource.RegistryRunOnce);
                ReadRunKey(raw, RegistryHive.LocalMachine, RunOnceKey, null, StartupSource.RegistryRunOnce);
                ReadRunKey(raw, RegistryHive.LocalMachine, RunOnce32Key, null, StartupSource.RegistryRunOnce);
                ct.ThrowIfCancellationRequested();
                ReadStartupFolder(raw, Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser);
                ReadStartupFolder(raw, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine);
                ct.ThrowIfCancellationRequested();
                ReadScheduledTasks(raw, ct);

                // Подписи и издатели — по одному разу на файл.
                var described = new Dictionary<string, FileInfoSummary>(StringComparer.OrdinalIgnoreCase);
                var result = new List<StartupEntry>(raw.Count);
                foreach (var entry in raw)
                {
                    ct.ThrowIfCancellationRequested();
                    var info = new FileInfoSummary("нет данных", false, false);
                    if (entry.ExecutablePath is { } exe)
                    {
                        if (described.TryGetValue(exe, out var known)) info = known;
                        else described[exe] = info = DescribeFile(exe);
                    }
                    bool windowsTask = entry.TaskPath?.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase) ?? false;
                    bool microsoftTask = entry.TaskPath?.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) ?? false;

                    // powershell, cmd, wscript и т.п. лежат в папке Windows, но запускают
                    // чужие скрипты: по самому файлу такая запись системной не считается,
                    // иначе любой скрипт в автозагрузке прятался бы среди системных.
                    if (IsScriptHost(entry.ExecutablePath))
                    {
                        result.Add(entry with
                        {
                            Publisher = $"запуск через {Path.GetFileName(entry.ExecutablePath)} — что именно запускается, видно в команде",
                            IsSystem = microsoftTask,
                            IsWindowsComponent = windowsTask
                        });
                        continue;
                    }

                    result.Add(entry with
                    {
                        Publisher = info.Publisher,
                        IsSystem = info.IsMicrosoft || info.InWindows || microsoftTask,
                        IsWindowsComponent = info.InWindows || windowsTask
                    });
                }
                return result;
            }, ct);

        private static string HiveLabel(RegistryHive hive) => hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";

        private static void ReadRunKey(List<StartupEntry> list, RegistryHive hive, string subKey, string? approvedSub, StartupSource source)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(subKey);
                if (key == null) return;
                using var approved = approvedSub == null ? null : root.OpenSubKey(ApprovedRoot + "\\" + approvedSub);
                var searchDirs = SearchDirs();

                foreach (string name in key.GetValueNames())
                {
                    if (string.IsNullOrEmpty(name)) continue; // значение «по умолчанию» Windows не запускает
                    if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command
                        || string.IsNullOrWhiteSpace(command))
                        continue;

                    list.Add(new StartupEntry
                    {
                        Source = source,
                        Name = name,
                        Command = command,
                        Location = HiveLabel(hive) + "\\" + subKey,
                        Enabled = source == StartupSource.RegistryRunOnce
                                  || IsApprovedEnabled(approved?.GetValue(name) as byte[]),
                        ExecutablePath = ExtractExecutable(command, File.Exists, searchDirs),
                        ApprovedHive = hive,
                        ApprovedSubKey = approvedSub,
                        ApprovedValueName = name
                    });
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Автозагрузка] Не прочитан раздел {HiveLabel(hive)}\\{subKey}: {ex.Message}");
            }
        }

        private static void ReadStartupFolder(List<StartupEntry> list, string folder, RegistryHive hive)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var approved = root.OpenSubKey(ApprovedRoot + @"\StartupFolder");
                var searchDirs = SearchDirs();

                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                    string command = file;
                    string? exe = file;
                    if (fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                        && ResolveShortcut(file) is { } target)
                    {
                        command = string.IsNullOrWhiteSpace(target.Arguments)
                            ? target.Path
                            : $"\"{target.Path}\" {target.Arguments}";
                        exe = ExtractExecutable($"\"{target.Path}\"", File.Exists, searchDirs);
                    }

                    list.Add(new StartupEntry
                    {
                        Source = StartupSource.StartupFolder,
                        Name = Path.GetFileNameWithoutExtension(fileName),
                        Command = command,
                        Location = file,
                        Enabled = IsApprovedEnabled(approved?.GetValue(fileName) as byte[]),
                        ExecutablePath = exe,
                        ApprovedHive = hive,
                        ApprovedSubKey = "StartupFolder",
                        ApprovedValueName = fileName
                    });
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Автозагрузка] Не прочитана папка автозагрузки: {ex.Message}");
            }
        }

        private static (string Path, string Arguments)? ResolveShortcut(string lnk)
        {
            object? shell = null;
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type == null) return null;
                shell = Activator.CreateInstance(type);
                if (shell == null) return null;
                dynamic shortcut = ((dynamic)shell).CreateShortcut(lnk);
                string path = shortcut.TargetPath ?? "";
                string args = shortcut.Arguments ?? "";
                Marshal.FinalReleaseComObject(shortcut);
                return path.Length == 0 ? null : (path, args);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Автозагрузка] Не прочитан ярлык {Path.GetFileName(lnk)}: {ex.Message}");
                return null;
            }
            finally
            {
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }

        private static void ReadScheduledTasks(List<StartupEntry> list, CancellationToken ct)
        {
            object? service = null;
            try
            {
                var type = Type.GetTypeFromProgID("Schedule.Service");
                if (type == null) return;
                service = Activator.CreateInstance(type);
                if (service == null) return;
                dynamic svc = service;
                svc.Connect();
                var searchDirs = SearchDirs();
                WalkFolder(list, svc.GetFolder("\\"), searchDirs, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Write($"[Автозагрузка] Планировщик заданий не прочитан: {ex.Message}");
            }
            finally
            {
                if (service != null && Marshal.IsComObject(service)) Marshal.FinalReleaseComObject(service);
            }
        }

        // Коллекции планировщика перебираются по индексу (с 1): перебор foreach по
        // dynamic-COM-коллекции в .NET работает не везде.
        private static void WalkFolder(List<StartupEntry> list, dynamic folder, IReadOnlyList<string> searchDirs, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                dynamic tasks = folder.GetTasks(EnumHidden);
                int count = tasks.Count;
                for (int i = 1; i <= count; i++)
                {
                    try
                    {
                        if (ReadTask(tasks[i], searchDirs) is StartupEntry entry) list.Add(entry);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Write($"[Автозагрузка] Задача не прочитана: {ex.Message}");
                    }
                }

                dynamic folders = folder.GetFolders(0);
                int folderCount = folders.Count;
                for (int i = 1; i <= folderCount; i++)
                    WalkFolder(list, folders[i], searchDirs, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Папки планировщика с закрытым доступом пропускаются.
                AppLogger.Write($"[Автозагрузка] Папка планировщика не прочитана: {ex.Message}");
            }
        }

        private static StartupEntry? ReadTask(dynamic task, IReadOnlyList<string> searchDirs)
        {
            dynamic definition = task.Definition;
            dynamic triggers = definition.Triggers;
            bool startsWithWindows = false;
            int triggerCount = triggers.Count;
            for (int t = 1; t <= triggerCount && !startsWithWindows; t++)
                startsWithWindows = IsStartupTrigger((int)triggers[t].Type);
            if (!startsWithWindows) return null;

            string command = "";
            dynamic actions = definition.Actions;
            int actionCount = actions.Count;
            for (int a = 1; a <= actionCount; a++)
            {
                dynamic action = actions[a];
                if ((int)action.Type != ActionExec) continue;
                string path = action.Path ?? "";
                string args = action.Arguments ?? "";
                command = path.Contains(' ') && !path.StartsWith('"') ? $"\"{path}\"" : path;
                if (args.Length > 0) command += " " + args;
                break;
            }
            // Задачи без запуска программы (COM-обработчики, письма) показывать нечем.
            if (command.Length == 0) return null;

            string taskPath = task.Path;
            return new StartupEntry
            {
                Source = StartupSource.ScheduledTask,
                Name = task.Name,
                Command = command,
                Location = "Планировщик заданий: " + taskPath,
                Enabled = task.Enabled,
                ExecutablePath = ExtractExecutable(command, File.Exists, searchDirs),
                TaskPath = taskPath
            };
        }

        /// <summary>
        /// Издатель файла и признак «Microsoft / системное». Файл в папке Windows —
        /// системный. Вне её «Microsoft» засчитывается только при действительной подписи
        /// Microsoft: сертификат из файла без проверки подделать можно, и тогда чужая
        /// программа спряталась бы среди системных.
        /// </summary>
        private sealed record FileInfoSummary(string Publisher, bool IsMicrosoft, bool InWindows);

        private static FileInfoSummary DescribeFile(string exe)
        {
            try
            {
                if (!File.Exists(exe)) return new("нет данных: файл не найден", false, false);
                bool inWindows = IsUnder(exe, Environment.GetFolderPath(Environment.SpecialFolder.Windows));

                string? signer = null;
                try
                {
#pragma warning disable SYSLIB0057
                    using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(exe));
#pragma warning restore SYSLIB0057
                    signer = OrganizationOf(certificate.Subject);
                }
                catch { /* встроенной подписи нет — системные файлы подписаны каталогом */ }

                bool microsoftSigned = !inWindows
                    && string.Equals(signer, "Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
                    && AuthenticodeVerifier.IsSignedByMicrosoft(exe, out _);

                string? company = null;
                try { company = FileVersionInfo.GetVersionInfo(exe).CompanyName?.Trim(); }
                catch { }

                // Без встроенной подписи издатель берётся из свойств файла — так и пишем:
                // файлы Windows подписаны каталогом, а не встроенной подписью.
                string publisher = signer != null
                    ? signer
                    : !string.IsNullOrEmpty(company) ? company + " (по свойствам файла)" : "нет данных";
                return new(publisher, microsoftSigned, inWindows);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Автозагрузка] Не проверен файл {Path.GetFileName(exe)}: {ex.Message}");
                return new("нет данных", false, false);
            }
        }

        // ── Выключение и включение ───────────────────────────────────────────────

        /// <summary>
        /// Выключает или включает запись. Реестр и папки — отметкой в StartupApproved
        /// (как Диспетчер задач), задачи — флагом Enabled. Ничего не удаляется.
        /// </summary>
        /// <returns>null — получилось; иначе причина для пользователя.</returns>
        public static Task<string?> SetEnabledAsync(StartupEntry entry, bool enabled) => Task.Run(() =>
        {
            if (!entry.CanToggle) return "Однократный запуск (RunOnce) не выключается — он сработает один раз и исчезнет сам.";
            try
            {
                if (entry.Source == StartupSource.ScheduledTask)
                    return SetTaskEnabled(entry.TaskPath, enabled);

                if (entry.ApprovedSubKey == null || entry.ApprovedValueName == null)
                    return "Для этой записи не известно, где хранится отметка включения.";

                using var root = RegistryKey.OpenBaseKey(entry.ApprovedHive, RegistryView.Registry64);
                using var key = root.CreateSubKey(ApprovedRoot + "\\" + entry.ApprovedSubKey, writable: true);
                key.SetValue(entry.ApprovedValueName, BuildApprovedValue(enabled, DateTime.UtcNow), RegistryValueKind.Binary);
                AppLogger.Write($"[Автозагрузка] {(enabled ? "Включено" : "Выключено")}: {entry.Name} ({entry.Location})");
                return (string?)null;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Автозагрузка] Не изменено «{entry.Name}»: {ex.Message}");
                return "Не получилось: " + ex.Message;
            }
        });

        private static string? SetTaskEnabled(string? taskPath, bool enabled)
        {
            if (string.IsNullOrEmpty(taskPath)) return "Путь задачи неизвестен.";
            object? service = null;
            try
            {
                var type = Type.GetTypeFromProgID("Schedule.Service");
                if (type == null) return "Планировщик заданий недоступен.";
                service = Activator.CreateInstance(type);
                if (service == null) return "Планировщик заданий недоступен.";
                dynamic svc = service;
                svc.Connect();
                dynamic task = svc.GetFolder("\\").GetTask(taskPath);
                task.Enabled = enabled;
                AppLogger.Write($"[Автозагрузка] Задача {(enabled ? "включена" : "выключена")}: {taskPath}");
                return null;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Автозагрузка] Задача {taskPath} не изменена: {ex.Message}");
                return ex is UnauthorizedAccessException
                    ? "Windows не даёт изменить эту задачу (она защищена системой)."
                    : "Не получилось: " + ex.Message;
            }
            finally
            {
                if (service != null && Marshal.IsComObject(service)) Marshal.FinalReleaseComObject(service);
            }
        }
    }
}
