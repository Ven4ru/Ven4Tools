using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// <c>Ven4Tools.Launcher.exe --revert-personalization</c> — откат всей «Персонализации» клиента
    /// перед удалением данных. Зовёт деинсталлятор: файлы «как было» лежат в данных Ven4Tools, и после
    /// их удаления вернуть тему панели, курсоры, обои и автозапуски было бы уже не по чему.
    ///
    /// Сам откат делает клиент (там вся логика и права администратора — экран блокировки пишется
    /// в HKLM): лаунчер находит его по своим настройкам и запускает с тем же ключом, без окон.
    /// Коды возврата: 0 — всё возвращено; 1 — что-то не вернулось (журнал клиента);
    /// 2 — клиент не найден; 3 — не запустился (в том числе отказ в окне контроля учётных записей);
    /// 4 — не уложился во время; 5 — клиент старше 7.2.0 и ключа не знает (откат не запускается).
    /// </summary>
    internal static class PersonalizationRevertRunner
    {
        public const string Switch = "--revert-personalization";
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

        /// <summary>Первая версия клиента с ключом <see cref="Switch"/>.</summary>
        internal static readonly Version FirstClientWithSwitch = new(7, 2, 0);

        /// <summary>
        /// Знает ли клиент ключ отката. Клиент до 7.2 его не знает и вместо отката открыл бы обычное
        /// окно — деинсталлятор ждал бы, пока человек его закроет (найдено на стенде 17.11). Версия —
        /// из свойств exe; нет версии или она не читается — считаем старым.
        /// </summary>
        internal static bool ClientSupportsRevert(string exe)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                var v = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
                return v >= FirstClientWithSwitch;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Режим командной строки: папка клиента — из настроек лаунчера в <paramref name="dataFolder"/>.</summary>
        public static int Run(string dataFolder) =>
            RunFor(ReadClientPath(Path.Combine(dataFolder, "launcher_settings.json")));

        /// <summary>Откатить через клиент в <paramref name="clientDir"/> (так же перед «Удалить клиент»).</summary>
        public static int RunFor(string? clientDir)
        {
            string? exe = string.IsNullOrWhiteSpace(clientDir) ? null : Path.Combine(clientDir, LauncherPaths.ClientExeName);
            if (exe == null || !File.Exists(exe))
            {
                LauncherLog.Write("[Персонализация] Откат перед удалением: клиент не найден — откатывать нечем");
                return 2;
            }
            if (!ClientSupportsRevert(exe))
            {
                LauncherLog.Write("[Персонализация] Откат перед удалением: клиент старше 7.2.0 не умеет откатывать без окна — пропущено");
                return 5;
            }
            try
            {
                // UseShellExecute: клиент требует прав администратора, запрос повышения покажет Windows.
                using var p = Process.Start(new ProcessStartInfo(exe, Switch) { UseShellExecute = true, WorkingDirectory = clientDir });
                if (p == null) return 3;
                if (!p.WaitForExit((int)Timeout.TotalMilliseconds))
                {
                    LauncherLog.Write("[Персонализация] Откат перед удалением не уложился в 3 минуты");
                    // Дальше удаляются файлы клиента и данные — зависший клиент держал бы их.
                    // Клиент запущен с повышенными правами: без них завершить его может не выйти.
                    try { p.Kill(entireProcessTree: true); }
                    catch (Exception ex) { LauncherLog.Write($"[Персонализация] Клиент после отката не завершён: {ex.Message}"); }
                    return 4;
                }
                LauncherLog.Write($"[Персонализация] Откат перед удалением: код {p.ExitCode}");
                return p.ExitCode == 0 ? 0 : 1;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                LauncherLog.Write("[Персонализация] Откат перед удалением отменён в окне контроля учётных записей");
                return 3;
            }
            catch (Exception ex)
            {
                LauncherLog.Write($"[Персонализация] Откат перед удалением не запустился: {ex.Message}");
                return 3;
            }
        }

        /// <summary>Из launcher_settings.json нужна только папка клиента (то же поле, что читает MainWindow.LoadSettings).</summary>
        private sealed class ClientPathOnly
        {
            public string? ClientPath { get; set; }
        }

        /// <summary>Папка клиента из настроек лаунчера; null — настроек нет или они не читаются.</summary>
        internal static string? ReadClientPath(string settingsPath)
        {
            try
            {
                if (!File.Exists(settingsPath)) return null;
                var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<ClientPathOnly>(File.ReadAllText(settingsPath));
                return settings?.ClientPath;
            }
            catch (Exception ex)
            {
                LauncherLog.Write($"[Персонализация] Настройки лаунчера не прочитаны: {ex.Message}");
                return null;
            }
        }
    }
}
