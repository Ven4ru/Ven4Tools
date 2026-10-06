using System;
using System.Collections.Generic;
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
                // Прежнее состояние запоминается до первой же правки — по нему твик
                // потом возвращается кнопкой «Вернуть» (см. DebloatUndoService).
                DebloatUndoService.Default.Capture(id, RegistryChangesOf(category, id), ServiceOf(category, id));

                bool ok = category switch
                {
                    "app"     => await RemoveAppxAsync(id, ct),
                    "privacy" => await ApplyPrivacyTweakAsync(id, ct),
                    "service" => await DisableServiceAsync(id, ct),
                    _         => false
                };

                // По журналу применённого потом проверяется, не вернуло ли твик
                // обновление Windows (см. DebloatDriftService).
                if (ok) DebloatAppliedJournal.Default.MarkApplied(id);
                return ok;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Деблоатер] Ошибка в ApplyItemAsync [{displayName}]: {ex.Message}");
                return false;
            }
        }

        // Имя пакета Appx — латиница/цифры/точка/дефис/подчёркивание. Сегодня все
        // значения — литералы из DebloatCatalog, но это единственное место, где
        // elevated-команда PowerShell собирается интерполяцией: любое расширение
        // источника твиков (пресеты, каталог, сайт) превратило бы его в инъекцию
        // с правами администратора. Поэтому проверка здесь, fail-closed.
        private static readonly System.Text.RegularExpressions.Regex AppxNamePattern =
            new(@"^[A-Za-z0-9._-]{1,128}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        // Экранирование для строкового литерала PowerShell в одинарных кавычках.
        private static string PsQuote(string value) => value.Replace("'", "''");

        public static async Task<bool> RemoveAppxAsync(string packageName, CancellationToken ct = default)
        {
            if (!AppxNamePattern.IsMatch(packageName))
            {
                AppLogger.Write($"[Деблоатер] Отклонено недопустимое имя пакета: {packageName}");
                return false;
            }
            string script = $"Get-AppxPackage -Name '*{packageName}*' | Remove-AppxPackage -ErrorAction SilentlyContinue; " +
                            $"Get-AppxProvisionedPackage -Online | Where-Object DisplayName -like '*{packageName}*' | Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue";
            return await RunPSAsync(script, ct);
        }

        // Что именно меняет каждый твик приватности в реестре. Таблица, а не ветки
        // switch: по ней же перед применением запоминается прежнее состояние для отката.
        private static readonly Dictionary<string, DebloatRegistryChange[]> PrivacyRegistry = new()
        {
            ["telemetry"] = new[]
            {
                new DebloatRegistryChange(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0),
                new DebloatRegistryChange(@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 0)
            },
            ["activity_history"] = new[]
            {
                new DebloatRegistryChange(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0),
                new DebloatRegistryChange(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0)
            },
            ["advertising_id"] = new[]
            {
                new DebloatRegistryChange(@"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0)
            },
            ["content_delivery"] = new[]
            {
                new DebloatRegistryChange(@"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", 0),
                new DebloatRegistryChange(@"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", 0)
            },
            ["cortana_registry"] = new[]
            {
                new DebloatRegistryChange(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0)
            },
            ["input_tracking"] = new[]
            {
                new DebloatRegistryChange(@"HKCU:\SOFTWARE\Microsoft\Input\TIPC", "Enabled", 0)
            }
        };

        /// <summary>Значения реестра, которые меняет твик; пусто, если он реестр не трогает.</summary>
        public static IReadOnlyList<DebloatRegistryChange> RegistryChangesOf(string category, string tweakId) =>
            category == "privacy" && PrivacyRegistry.TryGetValue(tweakId, out var changes)
                ? changes
                : Array.Empty<DebloatRegistryChange>();

        /// <summary>Служба, которую твик останавливает и отключает; null, если служб он не трогает.</summary>
        public static string? ServiceOf(string category, string tweakId) => (category, tweakId) switch
        {
            ("privacy", "diag_track")       => "DiagTrack",
            ("service", "svc_diagtrack")    => "DiagTrack",
            ("service", "svc_sysmain")      => "SysMain",
            ("service", "svc_dmwappushsvc") => "dmwappushservice",
            _                               => null
        };

        /// <summary>Можно ли вернуть твик точечно: он меняет реестр или службу, а не удаляет приложение.</summary>
        public static bool IsUndoable(string category, string tweakId) =>
            RegistryChangesOf(category, tweakId).Count > 0 || ServiceOf(category, tweakId) != null;

        public static async Task<bool> ApplyPrivacyTweakAsync(string tweakId, CancellationToken ct = default)
        {
            if (PrivacyRegistry.TryGetValue(tweakId, out var changes))
            {
                // Применяются все значения твика, даже если одно не записалось: так
                // было и раньше, и частично применённый твик лучше недоприменённого.
                bool all = true;
                foreach (var change in changes)
                    all &= await SetReg(change.Path, change.Name, change.Value, ct);
                return all;
            }

            if (tweakId == "diag_track")
                return await RunPSAsync("Stop-Service DiagTrack -Force -ErrorAction SilentlyContinue; Set-Service DiagTrack -StartupType Disabled -ErrorAction SilentlyContinue", ct);

            return false;
        }

        public static async Task<bool> DisableServiceAsync(string tweakId, CancellationToken ct = default)
        {
            string? svcName = ServiceOf("service", tweakId);
            if (svcName == null)
            {
                AppLogger.Write($"[Деблоатер] Неизвестный tweakId: {tweakId}");
                return false;
            }
            return await RunPSAsync($"Stop-Service {svcName} -Force -ErrorAction SilentlyContinue; Set-Service {svcName} -StartupType Disabled -ErrorAction SilentlyContinue", ct);
        }

        // Имена служб для отката приходят из файла записей, а не из литералов, поэтому
        // проверяются так же строго, как имена пакетов: команда собирается интерполяцией
        // и выполняется с правами администратора.
        private static readonly System.Text.RegularExpressions.Regex ServiceNamePattern =
            new(@"^[A-Za-z0-9_]{1,64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// Возвращает службе режим запуска, который был до твика (2 — авто, 3 — вручную,
        /// 4 — отключена), и запускает её, если она была автоматической.
        /// </summary>
        public static async Task<bool> RestoreServiceAsync(string service, int startMode, CancellationToken ct = default)
        {
            if (!ServiceNamePattern.IsMatch(service))
            {
                AppLogger.Write($"[Деблоатер] Отклонено недопустимое имя службы: {service}");
                return false;
            }
            string? startupType = startMode switch { 2 => "Automatic", 3 => "Manual", 4 => "Disabled", _ => null };
            if (startupType == null)
            {
                AppLogger.Write($"[Деблоатер] Неизвестный режим запуска службы {service}: {startMode}");
                return false;
            }

            string script = $"Set-Service {service} -StartupType {startupType} -ErrorAction Stop";
            if (startMode == 2) script += $"; Start-Service {service} -ErrorAction SilentlyContinue";
            return await RunPSAsync(script, ct);
        }

        private static async Task<bool> SetReg(string path, string name, int value, CancellationToken ct = default)
        {
            try
            {
                var psi = new ProcessStartInfo(TrustedExecutablePaths.PowerShellExe,
                    $"-NoProfile -ExecutionPolicy Bypass -Command \"If (!(Test-Path '{PsQuote(path)}')) {{ New-Item -Path '{PsQuote(path)}' -Force | Out-Null }}; Set-ItemProperty -Path '{PsQuote(path)}' -Name '{PsQuote(name)}' -Value {value}\"")
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
