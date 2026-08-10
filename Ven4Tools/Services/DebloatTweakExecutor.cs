using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Движок применения твиков очистки: удаление Appx-пакетов, правки реестра и
    /// отключение служб. Никакой связи с UI — принимает только категорию/идентификатор
    /// твика и возвращает признак успеха, поэтому вкладка «Очистка» остаётся тонкой
    /// оболочкой (фильтр, кнопки, прогресс), а системные операции живут отдельно.
    /// </summary>
    public static class DebloatTweakExecutor
    {
        /// <summary>
        /// Применяет один твик. <paramref name="category"/> — "app" (удаление Appx),
        /// "privacy" (правка реестра/служб приватности) или "service" (отключение службы).
        /// <paramref name="displayName"/> используется только в сообщениях журнала.
        /// </summary>
        public static async Task<bool> ApplyItemAsync(string category, string id, string displayName,
                                                      CancellationToken ct = default)
        {
            try
            {
                if (category == "app")
                    return await RemoveAppxAsync(id, ct);

                if (category == "privacy")
                    return await ApplyPrivacyTweakAsync(id, ct);

                if (category == "service")
                    return await DisableServiceAsync(id, ct);

                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Деблоатер] Ошибка в ApplyItemAsync [{displayName}]: {ex.Message}");
                return false;
            }
        }

        public static async Task<bool> RemoveAppxAsync(string packageName, CancellationToken ct = default)
        {
            string script = $"Get-AppxPackage -Name '*{packageName}*' | Remove-AppxPackage -ErrorAction SilentlyContinue; " +
                            $"Get-AppxProvisionedPackage -Online | Where-Object DisplayName -like '*{packageName}*' | Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue";
            return await RunPSAsync(script, ct);
        }

        public static async Task<bool> ApplyPrivacyTweakAsync(string tweakId, CancellationToken ct = default)
        {
            switch (tweakId)
            {
                case "telemetry":
                {
                    bool a = await SetReg(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, ct);
                    bool b = await SetReg(@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 0, ct);
                    return a && b;
                }
                case "activity_history":
                {
                    bool a = await SetReg(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0, ct);
                    bool b = await SetReg(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, ct);
                    return a && b;
                }
                case "advertising_id":
                    return await SetReg(@"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, ct);
                case "content_delivery":
                {
                    bool a = await SetReg(@"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", 0, ct);
                    bool b = await SetReg(@"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", 0, ct);
                    return a && b;
                }
                case "cortana_registry":
                    return await SetReg(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0, ct);
                case "input_tracking":
                    return await SetReg(@"HKCU:\SOFTWARE\Microsoft\Input\TIPC", "Enabled", 0, ct);
                case "diag_track":
                    return await RunPSAsync("Stop-Service DiagTrack -Force -ErrorAction SilentlyContinue; Set-Service DiagTrack -StartupType Disabled -ErrorAction SilentlyContinue", ct);
                default:
                    return false;
            }
        }

        public static async Task<bool> DisableServiceAsync(string tweakId, CancellationToken ct = default)
        {
            string? svcName = tweakId switch
            {
                "svc_diagtrack"     => "DiagTrack",
                "svc_sysmain"       => "SysMain",
                "svc_dmwappushsvc"  => "dmwappushservice",
                _                   => null
            };
            if (svcName == null)
            {
                AppLogger.Write($"[Деблоатер] Неизвестный tweakId: {tweakId}");
                return false;
            }
            return await RunPSAsync($"Stop-Service {svcName} -Force -ErrorAction SilentlyContinue; Set-Service {svcName} -StartupType Disabled -ErrorAction SilentlyContinue", ct);
        }

        private static async Task<bool> SetReg(string path, string name, int value, CancellationToken ct = default)
        {
            try
            {
                var psi = new ProcessStartInfo(TrustedExecutablePaths.PowerShellExe,
                    $"-NoProfile -ExecutionPolicy Bypass -Command \"If (!(Test-Path '{path}')) {{ New-Item -Path '{path}' -Force | Out-Null }}; Set-ItemProperty -Path '{path}' -Name '{name}' -Value {value}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;

                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();

                // Тайм-аут 5 секунд: запись в реестр не должна блокировать процесс надолго.
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await p.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(true); } catch { }
                    AppLogger.Write($"[Деблоатер] SetReg: тайм-аут или отмена [{path}\\{name}]");
                    return false;
                }

                await Task.WhenAll(outTask, errTask);
                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Деблоатер] Ошибка SetReg [{path}\\{name}]: {ex.Message}");
                return false;
            }
        }

        private static async Task<bool> RunPSAsync(string script, CancellationToken ct = default)
        {
            try
            {
                var psi = new ProcessStartInfo(TrustedExecutablePaths.PowerShellExe,
                    $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;

                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();

                // Тайм-аут: Remove-AppxPackage/Remove-AppxProvisionedPackage и
                // операции со службами умеют зависать. Без ограничения весь цикл
                // «Применить» блокировался бы навсегда без обратной связи. По образцу
                // SetReg, но с более щедрым лимитом под удаление Appx-пакетов.
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));
                try
                {
                    await p.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    AppLogger.Write("[Деблоатер] RunPSAsync: тайм-аут или отмена — процесс PowerShell завершён принудительно");
                    return false;
                }

                await Task.WhenAll(outTask, errTask);
                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Деблоатер] Ошибка RunPSAsync: {ex.Message}");
                return false;
            }
        }
    }
}
