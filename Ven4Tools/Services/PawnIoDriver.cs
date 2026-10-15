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
        /// <summary>
        /// Установка и управление драйвером в этой сборке. Выключено (решение владельца
        /// 15.10.2026): пока конфликты драйвера с античитами не проверены на играх, в
        /// программе экспериментальный модуль без драйвера — без кнопки установки, без
        /// включения и выключения устройства. Если PawnIO уже поставила другая программа
        /// (FanControl и т. п.), библиотека датчиков им пользуется только на чтение, а сам
        /// драйвер Ven4Tools не трогает. Полная версия — ветка feat/hardware-sensors (PR №108).
        /// </summary>
        public static bool FeatureEnabled { get; } = false;

        public const string WingetId = "namazso.PawnIO";

        /// <summary>Windows 10 2004 (19041): на более старых сборках PawnIO до 2.2.0 вызывал синий экран.</summary>
        public const int MinWindowsBuild = 19041;
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

        // ── Драйвер «только когда нужен» ─────────────────────────────────────────
        // Проверено на стенде 15.10.2026: отключение устройства ROOT\PAWNIO\0000 выгружает
        // драйвер сразу (служба останавливается за десятки миллисекунд), включение — грузит
        // обратно; отключённое устройство не грузится и после перезагрузки. Поэтому по
        // умолчанию устройство включено только пока Ven4Tools читает датчики процессора:
        // античиты, которые смотрят драйверы с момента загрузки Windows (Vanguard), после
        // перезагрузки его не увидят, если датчиками в этом сеансе не пользовались.

        private const uint ConfigFlagDisabled = 0x1;

        /// <summary>Устройство драйвера включено (выключенное не грузится ни сейчас, ни при старте Windows).</summary>
        public static bool IsDeviceEnabled()
        {
            foreach (string instance in DeviceInstances())
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instance}");
                if (key?.GetValue("ConfigFlags") is int flags && (flags & ConfigFlagDisabled) != 0) continue;
                if (key != null) return true;
            }
            return false;
        }

        /// <summary>Загружался ли драйвер после последнего старта Windows (им или нами).</summary>
        public static bool LoadedSinceBoot()
        {
            if (IsRunning()) return true;
            var bootUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
            return ProfileService.Current.PawnIoLastEnabledUtc is { } last && last > bootUtc;
        }

        public static async Task<bool> EnableDeviceAsync(IKitCommandRunner runner, CancellationToken ct = default)
        {
            bool ok = true;
            foreach (string instance in DeviceInstances())
            {
                var (code, _) = await runner.RunAsync(TrustedExecutablePaths.PnpUtilExe, new[] { "/enable-device", instance }, TimeSpan.FromSeconds(30), ct);
                AppLogger.Write($"[PawnIO] pnputil /enable-device {instance}: код {code}");
                ok &= code == 0;
            }
            for (int i = 0; i < 20 && !IsRunning(); i++) await Task.Delay(100, ct);
            ProfileService.Current.PawnIoLastEnabledUtc = DateTime.UtcNow;
            ProfileService.Save();
            return ok && IsRunning();
        }

        public static async Task<bool> DisableDeviceAsync(IKitCommandRunner runner, CancellationToken ct = default)
        {
            bool ok = true;
            foreach (string instance in DeviceInstances())
            {
                var (code, _) = await runner.RunAsync(TrustedExecutablePaths.PnpUtilExe, new[] { "/disable-device", instance }, TimeSpan.FromSeconds(30), ct);
                AppLogger.Write($"[PawnIO] pnputil /disable-device {instance}: код {code}");
                ok &= code == 0;
            }
            return ok;
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
            if (!IsInstalled()) await InstallFromMirrorAsync(ct);

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

        /// <summary>
        /// winget не скачал установщик (GitHub из России открывается через раз) — та же
        /// версия с нашего CDN: сумма из <see cref="MirrorDownload"/>, подпись автора, тихая установка.
        /// </summary>
        private static async Task InstallFromMirrorAsync(CancellationToken ct)
        {
            string setup = Path.Combine(Path.GetTempPath(), "Ven4Tools", MirrorDownload.PawnIo.FileName);
            if (!await MirrorDownload.DownloadAsync(MirrorDownload.PawnIo, setup, ct)) return;
            try
            {
                if (!AuthenticodeVerifier.IsSignedByOrganization(setup, new[] { "namazso" }, out string error))
                {
                    AppLogger.Write($"[PawnIO] установщик с CDN не запущен: подпись не подтверждена ({error})");
                    return;
                }
                var psi = new System.Diagnostics.ProcessStartInfo(setup) { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("-install");
                psi.ArgumentList.Add("-silent");
                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null) return;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                await process.WaitForExitAsync(timeout.Token);
                AppLogger.Write($"[PawnIO] установщик с CDN: код {process.ExitCode}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                AppLogger.Write("[PawnIO] установщик с CDN не уложился в 3 минуты");
            }
            finally
            {
                try { File.Delete(setup); } catch { }
            }
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
