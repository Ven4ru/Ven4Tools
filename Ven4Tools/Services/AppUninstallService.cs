using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Ven4Tools.Services
{
    /// <summary>Итог удаления: успех или читаемая причина неудачи.</summary>
    public sealed record UninstallResult(bool Ok, string? Reason)
    {
        public static readonly UninstallResult Success = new(true, null);
        public static UninstallResult Fail(string reason) => new(false, reason);
    }

    // Деинсталляция: winget uninstall по ID → сканирование реестра UninstallString
    // по DisplayName → тихий запуск (msiexec /x /quiet или NSIS/Inno /S /SILENT).
    // Перенесено из InstalledTab.xaml.cs (2026-07-17), чтобы карточка приложения
    // в каталоге могла переиспользовать ту же логику вместо копирования.
    public static class AppUninstallService
    {
        // Сколько ждём winget uninstall. Деинсталлятор, который игнорирует тихий режим
        // и открывает своё окно (AnyDesk на стенде 16.10.2026), держит его весь срок.
        internal static readonly TimeSpan WingetUninstallTimeout = TimeSpan.FromMinutes(15);
        // Удаление, которое столько времени не читает и не пишет на диск, почти
        // наверняка ждёт ответа в окне — дальше ждать бесполезно.
        internal static readonly TimeSpan WingetUninstallIdle = TimeSpan.FromMinutes(4);

        // Сколько ждём копию деинсталлятора во временной папке (NSIS копирует себя в
        // %TEMP%\~nsu*.tmp и сразу возвращает управление) и сколько ждём, пока запись
        // программы исчезнет из реестра.
        private static readonly TimeSpan TempUninstallerWait = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan RegistryGoneWait = TimeSpan.FromSeconds(30);
        // Сколько ещё ждём деинсталлятор после того, как запись программы пропала из
        // реестра. AnyDesk (стенд 17.10.2026) всё удаляет за полминуты, а потом держит
        // окно «AnyDesk был удалён… OK» — раньше его ждали до сторожа winget и до
        // двухминутного предела, а итог показывали как «не удалено».
        internal static readonly TimeSpan HungAfterRemovalGrace = TimeSpan.FromSeconds(30);

        public static async Task<bool> TryUninstallAsync(string? wingetId, string displayName)
            => (await UninstallAsync(wingetId, displayName)).Ok;

        /// <summary>
        /// Удаление с причиной неудачи. Раньше любая неудача показывалась как
        /// «деинсталлятор не найден» — в том числе когда он нашёлся, но завис или
        /// завершился с ошибкой. Найдено на стенде 16.10.2026 при удалении всего каталога:
        /// <list type="bullet">
        /// <item>успех по коду выхода ещё не значит, что программа удалена — копия
        /// деинсталлятора во временной папке могла остаться ждать ответа в своём окне;</item>
        /// <item>такие осиротевшие копии копились десятками и держали блокировку
        /// установщика Windows — все следующие удаления падали за секунды.</item>
        /// </list>
        /// </summary>
        public static async Task<UninstallResult> UninstallAsync(string? wingetId, string displayName)
        {
            var startedUtc = DateTime.UtcNow;
            bool wasInRegistry = FindUninstallString(displayName) != null;
            var result = await UninstallCoreAsync(wingetId, displayName, wasInRegistry);

            await WaitForTempUninstallersAsync(startedUtc, TempUninstallerWait);

            if (result.Ok && wasInRegistry && !await GoneFromRegistryAsync(displayName))
                result = UninstallResult.Fail(
                    "Деинсталлятор сообщил об успехе, но программа осталась в системе — вероятно, он ждёт ответа в своём окне.");

            int killed = KillTempUninstallers(startedUtc);
            if (killed > 0)
                AppLogger.Write($"[AppUninstallService] Закрыто зависших копий деинсталлятора после «{displayName}»: {killed}");
            return result;
        }

        private static async Task<bool> GoneFromRegistryAsync(string displayName)
        {
            var deadline = DateTime.UtcNow + RegistryGoneWait;
            while (true)
            {
                if (await Task.Run(() => FindUninstallString(displayName)) == null) return true;
                if (DateTime.UtcNow >= deadline) return false;
                await Task.Delay(2000);
            }
        }

        /// <summary>Копии деинсталляторов во временной папке, запущенные после начала удаления.</summary>
        private static System.Collections.Generic.List<Process> TempUninstallers(DateTime sinceUtc)
        {
            var list = new System.Collections.Generic.List<Process>();
            string temp = Path.GetFullPath(Path.GetTempPath());
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.StartTime.ToUniversalTime() >= sinceUtc &&
                        (p.MainModule?.FileName ?? "").StartsWith(temp, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(p);
                        continue;
                    }
                }
                catch { }
                p.Dispose();
            }
            return list;
        }

        private static async Task WaitForTempUninstallersAsync(DateTime sinceUtc, TimeSpan limit)
        {
            var deadline = DateTime.UtcNow + limit;
            while (DateTime.UtcNow < deadline)
            {
                var running = TempUninstallers(sinceUtc);
                int count = running.Count;
                foreach (var p in running) p.Dispose();
                if (count == 0) return;
                await Task.Delay(2000);
            }
        }

        private static int KillTempUninstallers(DateTime sinceUtc)
        {
            int killed = 0;
            foreach (var p in TempUninstallers(sinceUtc))
            {
                using (p)
                {
                    try { p.Kill(entireProcessTree: true); killed++; } catch { }
                }
            }
            return killed;
        }

        /// <summary>
        /// Запускает удаление и, пока оно идёт, следит за записью программы в реестре: если
        /// она пропала, а деинсталлятор через <see cref="HungAfterRemovalGrace"/> всё ещё не
        /// завершился, — он ждёт нажатия в своём последнем окне, и удаление обрывается
        /// через переданный токен.
        /// </summary>
        private static async Task<T> WithRemovalWatchAsync<T>(string displayName, bool watch, Func<CancellationToken, Task<T>> run)
        {
            using var hung = new CancellationTokenSource();
            using var stop = new CancellationTokenSource();
            var watcher = watch ? CancelWhenGoneAsync(displayName, hung, stop.Token) : Task.CompletedTask;
            try { return await run(hung.Token); }
            finally
            {
                stop.Cancel();
                await watcher;
            }
        }

        private static async Task CancelWhenGoneAsync(string displayName, CancellationTokenSource hung, CancellationToken stop)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(3000, stop);
                    if (await Task.Run(() => FindUninstallString(displayName), stop) != null) continue;
                    await Task.Delay(HungAfterRemovalGrace, stop);
                    AppLogger.Write($"[AppUninstallService] «{displayName}» уже удалена, а деинсталлятор ещё ждёт в своём окне — закрываем его");
                    hung.Cancel();
                    return;
                }
            }
            catch (OperationCanceledException) { }
        }

        private static async Task<UninstallResult> UninstallCoreAsync(string? wingetId, string displayName, bool wasInRegistry)
        {
            // Деинсталлятор из числа известных (AIMP, AnyDesk): winget вызывает его с ключами,
            // на которые тот открывает окно, и минуты уходят на ожидание сторожа — сразу
            // запускаем его из реестра с настоящими ключами тихого удаления.
            var known = await Task.Run(() => FindUninstallString(displayName));
            if (known is { FromMachineHive: true, Quiet: false } k &&
                ParseUninstallCommand(k.Command) is { Error: null } parsed && KnownSilentArgs(parsed.File) != null)
            {
                var direct = await WithRemovalWatchAsync(displayName, true,
                    hung => RunUninstallStringAsync(k.Command, false, hung));
                return direct.Ok
                    ? UninstallResult.Success
                    : UninstallResult.Fail(direct.Reason ?? "Деинсталлятор завершился с ошибкой.");
            }

            // Попытка 1: winget uninstall по ID (работает для пакетов с непустым Source).
            // Аргументы через ArgumentList (как везде в кодовой базе), не строковую
            // интерполяцию — .NET сам экранирует каждый токен, устраняя поверхность
            // инъекции. ValidateId — та же defense-in-depth проверка id, что перед
            // winget show в AvailabilityChecker.
            bool wingetFoundNothing = false;
            string? wingetFailure = null;
            // winget list обрезает длинный ID многоточием («Adobe.Acrobat.Reader.64-…»),
            // и такой ID раньше просто пропускался. Обрезанный ID ищем без --exact:
            // winget сопоставит начало ID, а при нескольких совпадениях откажется
            // (APPINSTALLER_CLI_ERROR_MULTIPLE_APPLICATIONS_FOUND), ничего не тронув.
            bool truncatedId = false;
            if (!string.IsNullOrWhiteSpace(wingetId) && wingetId.EndsWith('…'))
            {
                string prefix = wingetId.TrimEnd('…');
                wingetId = prefix.Length >= 6 && !prefix.Contains('…') ? prefix : null;
                truncatedId = wingetId != null;
            }
            // Локальные ID («MSIX\…», «ARP\User\…») — программы без пакета в источниках
            // winget; раньше такие строки «Установленных» удалить было нельзя вовсе.
            if (!string.IsNullOrWhiteSpace(wingetId) && !wingetId.Contains('…') &&
                (CommandLineGuard.ValidateId(wingetId) || CommandLineGuard.ValidateLocalPackageId(wingetId)))
            {
                // --exact: без него --id ищет по подстроке, и при двух похожих пакетах
                // (Mozilla.Firefox / Mozilla.Firefox.ESR) winget либо откажется, либо
                // тронет не тот.
                // Таймаут явно: по умолчанию RunAsync ждёт 120 с и убивает дерево
                // процессов — удаление крупного пакета обрывалось посреди работы деинсталлятора,
                // а следующая попытка через реестр запускалась поверх недоудалённого.
                // 15 минут — как у обновления одного пакета.
                var args = truncatedId
                    ? WingetArgs.Query("uninstall", "--id", wingetId, "--silent")
                    : WingetArgs.Query("uninstall", "--id", wingetId, "--exact", "--silent");
                var (exitCode, _) = await WithRemovalWatchAsync(displayName, wasInRegistry,
                    hung => WingetRunner.RunAsync(args, WingetUninstallTimeout, hung, WingetUninstallIdle));
                // Коды «удалено, нужна перезагрузка» — тоже успех. Раньше успехом был только 0:
                // удаление уходило в запасной путь через реестр, деинсталлятор запускался
                // повторно, а итог показывался как неудача.
                if (exitCode == 0 || WingetErrorMapper.IsSuccessWithReboot(exitCode))
                {
                    if (exitCode != 0)
                        AppLogger.Write($"[AppUninstallService] «{displayName}» удалена, для завершения нужна перезагрузка");
                    return UninstallResult.Success;
                }
                if (exitCode == WingetErrorMapper.AdminContextProhibited)
                {
                    // Программа поставлена в профиль пользователя: winget из-под
                    // администратора её не удаляет. Повторяем тот же winget без повышения прав.
                    var unelevated = await RunWingetUninstallUnelevatedAsync(args, displayName);
                    if (unelevated.Ok) return UninstallResult.Success;
                    wingetFailure = unelevated.Reason;
                }
                else wingetFailure = exitCode == -1
                    ? "Удаление через winget прервано: деинсталлятор перестал подавать признаки работы — вероятно, ждёт ответа в своём окне."
                    : "winget: " + WingetErrorMapper.MapExitCode(exitCode);
                // 0x8A150014 = winget не нашёл пакет с таким ID. Это ещё не «не установлено»:
                // приложение могло быть поставлено мимо winget или под другим ID — раньше
                // здесь сразу возвращался успех, и интерфейс писал «удалено», хотя
                // приложение оставалось в системе. Решает поиск в реестре ниже.
                wingetFoundNothing = exitCode == unchecked((int)0x8A150014);
            }

            // Попытка 2: найти строку UninstallString в реестре по DisplayName.
            var found = await Task.Run(() => FindUninstallString(displayName));
            // Ни winget, ни реестр о приложении не знают — удалять действительно нечего.
            // Запись была до удаления и пропала — программа удалена, даже если winget
            // пришлось оборвать: деинсталлятор ждал в последнем окне (AnyDesk).
            if (found == null)
                return wingetFoundNothing || wasInRegistry
                    ? UninstallResult.Success
                    : UninstallResult.Fail(wingetFailure ?? "Деинсталлятор не найден ни в winget, ни в реестре.");

            // Запись из HKCU не запускаем — см. развёрнутое обоснование в комментарии
            // к RunUninstallStringAsync. Возвращаем false, вызывающий код покажет
            // обычное «не удалось удалить», а причина остаётся в логе.
            if (!found.Value.FromMachineHive)
            {
                AppLogger.Write(
                    $"[AppUninstallService] ⚠ Команда деинсталляции для «{displayName}» найдена только в HKCU — " +
                    "запуск отменён: этот куст реестра доступен на запись непривилегированному процессу, " +
                    "а клиент запустил бы её с правами администратора");
                // С повышенными правами её запускать нельзя, а без них — можно: процесс
                // получает ровно права пользователя, которому этот раздел и так доступен.
                var userRun = await RunUnelevatedUninstallStringAsync(found.Value.Command, found.Value.Quiet, displayName);
                return userRun.Ok
                    ? UninstallResult.Success
                    : UninstallResult.Fail(userRun.Reason ?? wingetFailure ?? "Деинсталлятор из профиля пользователя не удалил программу.");
            }

            var run = await WithRemovalWatchAsync(displayName, true,
                hung => RunUninstallStringAsync(found.Value.Command, found.Value.Quiet, hung));
            return run.Ok
                ? UninstallResult.Success
                : UninstallResult.Fail(run.Reason ?? wingetFailure ?? "Деинсталлятор завершился с ошибкой.");
        }

        /// <summary>
        /// Ищет UninstallString по отображаемому имени и сообщает, из какого куста
        /// реестра он взят. Куст важен не для самого поиска, а для доверия к результату:
        /// HKLM пишется только администратором, HKCU — любым процессом текущего
        /// пользователя (см. <see cref="RunUninstallStringAsync"/>).
        /// </summary>
        internal static (string Command, bool FromMachineHive, bool Quiet)? FindUninstallString(string displayName)
        {
            string[] keys = {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            // HKCU покрывает user-scope установки (Chrome/Discord/VS Code и т.п.
            // при выборе "только для меня"), которых нет в HKLM. Куст сканируется,
            // чтобы отличить «приложение вообще не найдено» от «найдено, но источнику
            // нельзя доверять» и написать это в лог — исполняются только записи HKLM.
            var hives = new[] { (Key: Registry.LocalMachine, Machine: true),
                                (Key: Registry.CurrentUser,  Machine: false) };

            // Имя из winget list тоже бывает обрезано многоточием: тогда ищем по началу
            // имени и принимаем только единственное совпадение.
            bool truncatedName = displayName.EndsWith('…');
            string namePrefix = displayName.TrimEnd('…');
            (string Command, bool FromMachineHive, bool Quiet)? prefixHit = null;
            int prefixHits = 0;

            foreach (var hive in hives)
            foreach (var keyPath in keys)
            {
                using var root = hive.Key.OpenSubKey(keyPath);
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var entry = root.OpenSubKey(sub);
                    if (entry == null) continue;
                    var name = entry.GetValue("DisplayName")?.ToString();
                    if (name != null && name.Equals(displayName, StringComparison.OrdinalIgnoreCase))
                    {
                        var cmd = PreferredUninstallCommand(entry, out bool quiet);
                        if (!string.IsNullOrWhiteSpace(cmd))
                            return (cmd, hive.Machine, quiet);
                    }
                    else if (truncatedName && namePrefix.Length >= 6 && name != null &&
                             name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var cmd = PreferredUninstallCommand(entry, out bool quiet);
                        if (!string.IsNullOrWhiteSpace(cmd)) { prefixHit = (cmd, hive.Machine, quiet); prefixHits++; }
                    }
                }
            }
            return prefixHits == 1 ? prefixHit : null;
        }

        /// <summary>
        /// Запускает найденную команду деинсталляции. Вызывается ТОЛЬКО для записей
        /// из HKLM — записи из HKCU отсекаются вызывающим кодом.
        /// </summary>
        /// <remarks>
        /// Почему HKCU сюда не пускается. Клиент всегда работает с правами
        /// администратора (app.manifest), а дочерний процесс наследует повышенный
        /// токен родителя — простое снятие <c>Verb = "runas"</c> его НЕ понижает,
        /// поэтому «запустить без прав» здесь технически нечем. HKCU при этом доступен
        /// на запись любому непривилегированному процессу текущего пользователя:
        /// подложив туда DisplayName удаляемого приложения и произвольный
        /// UninstallString, можно было бы добиться исполнения своего кода с правами
        /// администратора без единого запроса UAC. Это тот же класс
        /// «непривилегированные данные → elevated-действие», от которого намеренно
        /// защищён <see cref="AppLaunchResolver"/> (там HKCU не читается вовсе), и
        /// то же fail-closed правило: нет доверенного источника — не делаем ничего.
        /// Цена — user-scope приложения (установленные «только для меня») этим путём
        /// не удаляются; для них штатный путь остаётся winget uninstall выше.
        /// </remarks>
        // Сколько ждём, пока программа исчезнет после запуска удаления без прав
        // администратора: код выхода такого процесса клиенту не виден, итог — по факту.
        private static readonly TimeSpan UnelevatedWait = TimeSpan.FromMinutes(5);

        private static async Task<(bool Ok, string? Reason)> RunWingetUninstallUnelevatedAsync(
            string[] args, string displayName)
        {
            string? winget = TrustedExecutablePaths.ResolveWinget();
            if (winget == null) return (false, "winget не найден.");
            string line = string.Join(" ", args.Select(QuoteArg));
            var startedUtc = DateTime.UtcNow;
            if (!UnelevatedLauncher.TryStart(winget, line))
                return (false, "winget не удаляет программы из профиля пользователя от имени администратора, а запустить его без прав администратора не удалось.");
            AppLogger.Write($"[AppUninstallService] «{displayName}» установлена в профиль пользователя — winget uninstall запущен без прав администратора");
            await WaitUnelevatedAsync("winget", null, startedUtc, displayName, WingetUninstallTimeout);
            return await GoneFromRegistryAsync(displayName)
                ? (true, null)
                : (false, "winget без прав администратора не удалил программу из профиля пользователя.");
        }

        private static async Task<(bool Ok, string? Reason)> RunUnelevatedUninstallStringAsync(string uninstallString, bool quiet, string displayName)
        {
            var (file, arguments, error) = ParseUninstallCommand(uninstallString, quiet);
            if (error != null) return (false, error);
            var startedUtc = DateTime.UtcNow;
            if (!UnelevatedLauncher.TryStart(file, arguments))
                return (false, "Не удалось запустить деинсталлятор без прав администратора.");
            AppLogger.Write($"[AppUninstallService] Команда удаления «{displayName}» из профиля пользователя запущена без прав администратора");
            var deadline = DateTime.UtcNow + UnelevatedWait;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(3000);
                if (await Task.Run(() => FindUninstallString(displayName)) == null) return (true, null);
            }
            // msiexec не трогаем: под тем же именем работает служба установщика Windows.
            if (!string.Equals(file, TrustedExecutablePaths.MsiExec, StringComparison.OrdinalIgnoreCase))
                KillLeftovers(Path.GetFileNameWithoutExtension(file), file, startedUtc, displayName);
            return (false, "Деинсталлятор из профиля пользователя не завершил удаление — вероятно, ждёт ответа в своём окне.");
        }

        /// <summary>
        /// QuietUninstallString — готовая команда тихого удаления от самой программы;
        /// если её нет, обычная UninstallString (к ней ключ тихого режима подбирается сам).
        /// </summary>
        private static string? PreferredUninstallCommand(RegistryKey entry, out bool quiet)
        {
            var quietCommand = entry.GetValue("QuietUninstallString")?.ToString();
            quiet = !string.IsNullOrWhiteSpace(quietCommand);
            return quiet ? quietCommand : entry.GetValue("UninstallString")?.ToString();
        }

        /// <summary>Деинсталлятор Inno Setup: unins000.exe, unins001.exe…</summary>
        internal static bool IsInnoUninstaller(string exe)
            => Regex.IsMatch(Path.GetFileName(exe), @"^unins\d{3}\.exe$", RegexOptions.IgnoreCase);

        /// <summary>
        /// Деинсталляторы, которым ни /S, ни ключи winget не помогают (стенд 16–17.10.2026),
        /// и их настоящие ключи тихого удаления; null — деинсталлятор не из их числа.
        /// <list type="bullet">
        /// <item>AIMP: на /S и /SILENT открывает окно «Select Language» и ждёт — тихо
        /// удаляет только с /AUTO /SILENT;</item>
        /// <item>AnyDesk: --uninstall (так и в реестре, и у winget) открывает окно
        /// подтверждения и ничего не удаляет; --remove удаляет сам и оставляет только
        /// окно «AnyDesk был удалён» — его закрывает <see cref="WithRemovalWatchAsync{T}"/>.</item>
        /// <item>Docker Desktop: удаляет тот же установщик командой uninstall, ключ тихого
        /// режима — --quiet (манифест winget-pkgs); с добавленным /S завершался с кодом -5.</item>
        /// </list>
        /// </summary>
        internal static string? KnownSilentArgs(string exe)
        {
            if (string.Equals(Path.GetFileName(exe), "AnyDesk.exe", StringComparison.OrdinalIgnoreCase)) return "--remove";
            if (string.Equals(Path.GetFileName(exe), "Docker Desktop Installer.exe", StringComparison.OrdinalIgnoreCase)) return "uninstall --quiet";
            if (IsAimpUninstaller(exe)) return "/AUTO /SILENT";
            return null;
        }

        /// <summary>Деинсталлятор AIMP: в свойствах файла — «AIMP Setup» от Artem Izmaylov.</summary>
        internal static bool IsAimpUninstaller(string exe)
        {
            try
            {
                if (!File.Exists(exe)) return false;
                var info = FileVersionInfo.GetVersionInfo(exe);
                return IsAimpUninstaller(info.FileDescription, info.CompanyName);
            }
            catch { return false; }
        }

        internal static bool IsAimpUninstaller(string? fileDescription, string? companyName)
            => string.Equals(fileDescription?.Trim(), "AIMP Setup", StringComparison.OrdinalIgnoreCase) &&
               (companyName ?? "").Contains("Izmaylov", StringComparison.OrdinalIgnoreCase);

        internal static string QuoteArg(string a)
            => a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

        /// <summary>
        /// Ждёт процесс, запущенный без прав администратора: пока он не завершится, пока
        /// запись программы не пропадёт из реестра (и ещё <see cref="HungAfterRemovalGrace"/>
        /// на последнее окно) или до предела — и снимает то, что от него осталось.
        /// </summary>
        /// <remarks>
        /// Раньше оставшийся процесс не снимался. Стенд 17.10.2026: деинсталлятор Cursor,
        /// запущенный winget без прав, запросил повышение, на окно UAC никто не ответил —
        /// и этот winget держал очередь winget: каждое следующее удаление ждало его все
        /// 15 минут, хотя сама программа удалялась за секунды.
        /// </remarks>
        private static async Task WaitUnelevatedAsync(string name, string? path, DateTime sinceUtc, string displayName, TimeSpan limit)
        {
            await Task.Delay(3000); // оболочке нужно время, чтобы процесс появился
            var deadline = DateTime.UtcNow + limit;
            DateTime? goneAt = null;
            while (DateTime.UtcNow < deadline && CountStartedSince(name, path, sinceUtc, kill: false) > 0)
            {
                if (goneAt == null && await Task.Run(() => FindUninstallString(displayName)) == null)
                    goneAt = DateTime.UtcNow;
                if (goneAt is { } gone && DateTime.UtcNow - gone >= HungAfterRemovalGrace) break;
                await Task.Delay(2000);
            }
            KillLeftovers(name, path, sinceUtc, displayName);
        }

        private static void KillLeftovers(string name, string? path, DateTime sinceUtc, string displayName)
        {
            int killed = CountStartedSince(name, path, sinceUtc, kill: true);
            if (killed > 0)
                AppLogger.Write($"[AppUninstallService] Закрыто оставшихся после «{displayName}» процессов {name}: {killed}");
        }

        /// <summary>
        /// Процессы с этим именем (и, если задан, с этим файлом), запущенные после
        /// <paramref name="sinceUtc"/>; с <paramref name="kill"/> — завершает каждый вместе с потомками.
        /// </summary>
        internal static int CountStartedSince(string name, string? path, DateTime sinceUtc, bool kill)
        {
            int count = 0;
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try
                    {
                        if (p.StartTime.ToUniversalTime() < sinceUtc.AddSeconds(-2)) continue;
                        if (path != null && !string.Equals(p.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase)) continue;
                        if (kill) p.Kill(entireProcessTree: true);
                        count++;
                    }
                    catch { }
                }
            }
            return count;
        }

        /// <summary>
        /// Разбор UninstallString в «файл + аргументы» тихого удаления. Для MSI —
        /// msiexec /x {код} /quiet /norestart; для остальных к аргументам добавляется /S,
        /// если там нет ключа тихого режима. null и причина — если запустить нечего.
        /// </summary>
        internal static (string File, string Args, string? Error) ParseUninstallCommand(string uninstallString, bool quiet = false)
        {
            string cmd = uninstallString.Trim();
            if (cmd.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase) ||
                cmd.StartsWith("msiexec", StringComparison.OrdinalIgnoreCase))
            {
                var productCode = Regex.Match(cmd, @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}");
                if (!productCode.Success)
                    return ("", "", "В команде удаления MSI нет кода продукта.");
                return (TrustedExecutablePaths.MsiExec, $"/x {productCode.Value} /quiet /norestart", null);
            }
            {
                string exe = cmd, args = "";
                if (cmd.StartsWith("\""))
                {
                    int end = cmd.IndexOf('"', 1);
                    if (end > 0) { exe = cmd.Substring(1, end - 1); args = cmd.Substring(end + 1).Trim(); }
                }
                else
                {
                    int searchFrom = 0;
                    int bestSplit = -1;
                    while (true)
                    {
                        int sp = cmd.IndexOf(' ', searchFrom);
                        if (sp < 0) break;
                        string candidate = cmd.Substring(0, sp);
                        if (File.Exists(candidate)) bestSplit = sp;
                        searchFrom = sp + 1;
                    }

                    if (bestSplit > 0)
                    {
                        exe = cmd.Substring(0, bestSplit);
                        args = cmd.Substring(bestSplit + 1).Trim();
                    }
                    else if (!File.Exists(cmd))
                    {
                        int sp = cmd.IndexOf(' ');
                        if (sp > 0) { exe = cmd.Substring(0, sp); args = cmd.Substring(sp + 1).Trim(); }
                    }
                }
                // Готовую тихую команду программы (QuietUninstallString) не трогаем:
                // лишний ключ ломает, например, Squirrel (Update.exe --uninstall -s).
                if (quiet) { }
                else if (KnownSilentArgs(exe) is { } known)
                    args = known;
                else if (IsInnoUninstaller(exe))
                {
                    // Inno Setup не понимает /S и показывает окно подтверждения
                    // (Cursor на стенде 16.10.2026) — ему нужен /VERYSILENT.
                    if (args.IndexOf("/VERYSILENT", StringComparison.OrdinalIgnoreCase) < 0)
                        args = ("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART " + args).Trim();
                }
                else if (!args.Contains("/S") && !args.Contains("/SILENT") && !args.Contains("/silent"))
                    args = "/S " + args;

                if (!File.Exists(exe)) return ("", "", "Файл деинсталлятора из реестра не найден на диске.");
                return (exe, args, null);
            }
        }

        private static async Task<(bool Ok, string? Reason)> RunUninstallStringAsync(string uninstallString, bool quiet, CancellationToken hung)
        {
            var (file, arguments, error) = ParseUninstallCommand(uninstallString, quiet);
            if (error != null) return (false, error);
            var p = Process.Start(new ProcessStartInfo(file, arguments)
                { UseShellExecute = true, Verb = "runas", CreateNoWindow = true });
            if (p == null) return (false, "Деинсталлятор не запустился.");
            using (p)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(hung);
                    cts.CancelAfter(TimeSpan.FromSeconds(120));
                    await p.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // entireProcessTree: деинсталляторы (msiexec /x, NSIS/Inno /S) порождают
                    // дочерние процессы — как и везде в кодовой базе при таймауте/отмене.
                    try { p.Kill(entireProcessTree: true); } catch { }
                    // Программа уже удалена, деинсталлятор только ждал нажатия «OK».
                    if (hung.IsCancellationRequested) return (true, null);
                    return (false, "Деинсталлятор не завершился за 2 минуты — вероятно, ждёт ответа в своём окне.");
                }
                // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED, 1641 = ERROR_SUCCESS_REBOOT_INITIATED —
                // удаление прошло успешно
                return p.ExitCode == 0 || p.ExitCode == 3010 || p.ExitCode == 1641
                    ? (true, null)
                    : (false, MsiExitCodes.Known.TryGetValue(p.ExitCode, out var m) ? m : $"Деинсталлятор завершился с кодом {p.ExitCode}.");
            }
        }
    }
}
