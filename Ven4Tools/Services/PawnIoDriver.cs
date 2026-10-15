using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Ven4Tools.Shared;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Драйвер PawnIO (namazso, открытый код, подпись Microsoft WHCP) — нужен только
    /// для датчиков процессора и платы. Программа его НЕ ставит сама: только по кнопке
    /// и после объяснения. Если его уже поставила другая программа (LibreHardwareMonitor,
    /// FanControl, OpenRGB), датчики просто работают.
    ///
    /// Проверено на стенде и на ПК с включённой целостностью памяти (15.10.2026):
    /// <c>winget uninstall</c> драйвер НЕ удаляет — пакет остаётся в хранилище драйверов,
    /// и после перезагрузки служба снова запущена. Поэтому <see cref="RemoveAsync"/>
    /// после winget доудаляет устройство, пакет драйвера и службу сам.
    /// </summary>
    public static class PawnIoDriver
    {
        public const string ServiceName = "PawnIO";
        public const string WingetId = "namazso.PawnIO";
        private const string DeviceEnumKey = @"SYSTEM\CurrentControlSet\Enum\ROOT\PAWNIO";

        // ServiceController.GetServices() служб драйверов ядра не видит (только обычные
        // службы) — проверено на ПК с установленным PawnIO. Запись службы в реестре есть
        // у тех и других.
        //
        // После удаления пакета драйвера Windows оставляет запись службы с DeleteFlag=1 и
        // отключённым запуском и стирает её при следующей перезагрузке. Такая служба уже
        // не установлена.
        public static bool IsInstalled()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
                return key?.GetValue("ImagePath") != null && !IsMarkedForDeletion(key);
            }
            catch { return false; }
        }

        /// <summary>Служба удалена, но её запись исчезнет только после перезагрузки.</summary>
        public static bool IsPendingDeletion()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
                return key != null && IsMarkedForDeletion(key);
            }
            catch { return false; }
        }

        private static bool IsMarkedForDeletion(RegistryKey key) => key.GetValue("DeleteFlag") is int flag && flag == 1;

        public static bool IsRunning()
        {
            try
            {
                using var service = new ServiceController(ServiceName);
                return service.Status == ServiceControllerStatus.Running;
            }
            catch { return false; }
        }

        /// <summary>Файл драйвера по записи службы; null — службы нет.</summary>
        public static string? DriverPath()
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            if (key?.GetValue("ImagePath") is not string image) return null;
            string path = image.Trim('"');
            if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
            if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path[12..]);
            else if (path.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path);
            return path;
        }

        public sealed record Result(bool Ok, string Message);

        public static async Task<Result> InstallAsync(CancellationToken ct = default)
        {
            var (code, output) = await WingetRunner.RunAsync(new[]
            {
                "install", "--id", WingetId, "--exact", "--source", "winget", "--silent",
                "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"
            }, TimeSpan.FromMinutes(5), ct);
            AppLogger.Write($"[PawnIO] winget install: код {code}");

            if (!IsInstalled())
                return new Result(false, code == -1
                    ? Tr("winget не найден или не ответил. Проверьте интернет и попробуйте ещё раз.")
                    : string.Format(Tr("Установка не удалась (winget, код {0})."), code));

            // У драйвера две подписи: встроенную в PawnIO.sys ставит автор (namazso,
            // проверено 15.10.2026), а подпись Microsoft WHCP стоит на каталоге пакета —
            // без неё 64-битная Windows драйвер ядра не загрузит. Здесь проверяем
            // встроенную: действительна и поставлена автором (или Microsoft, если в
            // будущей версии файл переподпишут).
            string? path = DriverPath();
            string error = "нет файла драйвера";
            if (path == null || !AuthenticodeVerifier.IsSignedByOrganization(path, new[] { "namazso", "Microsoft Corporation" }, out error))
            {
                AppLogger.Write($"[PawnIO] подпись драйвера не подтверждена ({path}): {error} — удаляю");
                await RemoveAsync(KitCommandRunner.Default, ct);
                return new Result(false, Tr("Подпись драйвера не подтвердилась — он удалён. Датчики процессора останутся недоступны."));
            }

            if (!IsRunning())
            {
                try
                {
                    using var service = new ServiceController(ServiceName);
                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                }
                catch (Exception ex) { AppLogger.Write(ex, "[PawnIO] запуск службы"); }
            }

            await InstallHistoryService.Instance.TrackAsync("pawnio", "PawnIO — драйвер датчиков", "winget", "Драйверы");
            return new Result(true, Tr("Драйвер датчиков установлен."));
        }

        /// <summary>Полное удаление: winget, затем устройство, пакет в хранилище драйверов и служба.</summary>
        public static async Task<Result> RemoveAsync(IKitCommandRunner runner, CancellationToken ct = default)
        {
            var (wingetCode, _) = await WingetRunner.RunAsync(new[]
            {
                "uninstall", "--id", WingetId, "--exact", "--silent",
                "--accept-source-agreements", "--disable-interactivity"
            }, TimeSpan.FromMinutes(3), ct);
            AppLogger.Write($"[PawnIO] winget uninstall: код {wingetCode}");

            string pnputil = TrustedExecutablePaths.PnpUtilExe;
            foreach (string instance in DeviceInstances())
            {
                var (code, _) = await runner.RunAsync(pnputil, new[] { "/remove-device", instance }, TimeSpan.FromMinutes(1), ct);
                AppLogger.Write($"[PawnIO] pnputil /remove-device {instance}: код {code}");
            }

            foreach (string inf in FindPublishedInfs(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF")))
            {
                var (code, _) = await runner.RunAsync(pnputil, new[] { "/delete-driver", inf, "/uninstall", "/force" }, TimeSpan.FromMinutes(2), ct);
                AppLogger.Write($"[PawnIO] pnputil /delete-driver {inf}: код {code}");
            }

            if (IsInstalled())
            {
                var (code, _) = await runner.RunAsync(TrustedExecutablePaths.ScExe, new[] { "delete", ServiceName }, TimeSpan.FromSeconds(30), ct);
                AppLogger.Write($"[PawnIO] sc delete: код {code}");
            }

            bool gone = !IsInstalled() && FindPublishedInfs(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF")).Count == 0;
            return gone
                ? new Result(true, IsPendingDeletion()
                    ? Tr("Драйвер датчиков удалён. Запись о нём Windows сотрёт при следующей перезагрузке.")
                    : Tr("Драйвер датчиков удалён полностью."))
                : new Result(false, Tr("Драйвер удалён не полностью — перезагрузите компьютер и нажмите «Удалить драйвер» ещё раз."));
        }

        private static IEnumerable<string> DeviceInstances()
        {
            using var key = Registry.LocalMachine.OpenSubKey(DeviceEnumKey);
            return key?.GetSubKeyNames().Select(n => $@"ROOT\PAWNIO\{n}").ToList() ?? new List<string>();
        }

        /// <summary>
        /// Опубликованные копии <c>pawnio.inf</c> в папке INF Windows (<c>oemNN.inf</c>).
        /// Ищем по содержимому, а не разбором вывода <c>pnputil /enum-drivers</c>: его
        /// подписи переведены на язык системы.
        /// </summary>
        internal static List<string> FindPublishedInfs(string infDirectory)
        {
            var result = new List<string>();
            if (!Directory.Exists(infDirectory)) return result;
            foreach (string file in Directory.EnumerateFiles(infDirectory, "oem*.inf"))
            {
                try
                {
                    string text = File.ReadAllText(file);
                    if (text.Contains("PawnIO.sys", StringComparison.OrdinalIgnoreCase))
                        result.Add(Path.GetFileName(file));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return result;
        }
    }
}
