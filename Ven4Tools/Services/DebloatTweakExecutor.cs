using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
                    "ads"     => await ApplyAdsTweakAsync(id, ct),
                    _         => false
                };

                // По журналу применённого потом проверяется, не вернуло ли твик
                // обновление Windows (см. DebloatDriftService).
                if (ok) DebloatAppliedJournal.Default.MarkApplied(id);
                return ok;
                // Запись отката при неуспехе остаётся намеренно: твик мог примениться
                // частично, и вернуть уже записанные значения можно только по ней.
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

        // ── Реклама и навязанный ИИ ──────────────────────────────────────────────
        // Значения сверены с документацией Microsoft (Policy CSP: WindowsAI,
        // NewsAndInterests, Feeds, WindowsCopilot, Search) и с набором Win11Debloat.
        // Политики (ветки …\Policies\…) блокируют соответствующие переключатели в
        // «Параметрах» с пометкой «управляется организацией» — это ожидаемо, «Вернуть»
        // удаляет значение и возвращает переключатель пользователю.
        private const string Cdm = @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
        private const string ExplorerAdvanced = @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        private const string CopilotPolicy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot";
        private const string WindowsAiPolicy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI";

        /// <summary>Пакет приложения Copilot — удаляется тем же путём, что и встроенные приложения.</summary>
        public const string CopilotAppId = "Microsoft.Copilot";

        /// <summary>Удаление OneDrive — отдельный путь со своей проверкой папок и откатом через winget.</summary>
        public const string OneDriveId = "ads_onedrive";

        private static readonly Dictionary<string, DebloatRegistryChange[]> AdsRegistry = new()
        {
            ["ads_start_recommendations"] = new[]
            {
                new DebloatRegistryChange(ExplorerAdvanced, "Start_IrisRecommendations", 0)
            },
            ["ads_account_notifications"] = new[]
            {
                new DebloatRegistryChange(ExplorerAdvanced, "Start_AccountNotifications", 0),
                new DebloatRegistryChange(@"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\SystemSettings\AccountNotifications", "EnableAccountNotifications", 0)
            },
            ["ads_start_suggestions"] = new[]
            {
                new DebloatRegistryChange(Cdm, "SubscribedContent-338388Enabled", 0)
            },
            ["ads_tips"] = new[]
            {
                new DebloatRegistryChange(Cdm, "SubscribedContent-338389Enabled", 0),
                new DebloatRegistryChange(Cdm, "SoftLandingEnabled", 0)
            },
            ["ads_settings_suggestions"] = new[]
            {
                new DebloatRegistryChange(Cdm, "SubscribedContent-338393Enabled", 0),
                new DebloatRegistryChange(Cdm, "SubscribedContent-353694Enabled", 0),
                new DebloatRegistryChange(Cdm, "SubscribedContent-353696Enabled", 0)
            },
            ["ads_welcome"] = new[]
            {
                new DebloatRegistryChange(Cdm, "SubscribedContent-310093Enabled", 0),
                new DebloatRegistryChange(@"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0)
            },
            ["ads_lock_screen"] = new[]
            {
                new DebloatRegistryChange(Cdm, "RotatingLockScreenOverlayEnabled", 0),
                new DebloatRegistryChange(Cdm, "SubscribedContent-338387Enabled", 0)
            },
            ["ads_tailored"] = new[]
            {
                new DebloatRegistryChange(@"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0)
            },
            ["ads_explorer_sync"] = new[]
            {
                new DebloatRegistryChange(ExplorerAdvanced, "ShowSyncProviderNotifications", 0)
            },
            ["ads_search_web"] = new[]
            {
                new DebloatRegistryChange(@"HKCU:\SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1),
                new DebloatRegistryChange(@"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0)
            },
            ["ads_search_highlights"] = new[]
            {
                new DebloatRegistryChange(@"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0)
            },
            ["ads_widgets"] = new[]
            {
                new DebloatRegistryChange(@"HKLM:\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0)
            },
            ["ads_news_feeds"] = new[]
            {
                new DebloatRegistryChange(@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Feeds", "EnableFeeds", 0)
            },
            ["ads_copilot"] = new[]
            {
                new DebloatRegistryChange(@"HKCU:\" + CopilotPolicy, "TurnOffWindowsCopilot", 1),
                new DebloatRegistryChange(@"HKLM:\" + CopilotPolicy, "TurnOffWindowsCopilot", 1),
                new DebloatRegistryChange(ExplorerAdvanced, "ShowCopilotButton", 0)
            },
            ["ads_recall"] = new[]
            {
                new DebloatRegistryChange(@"HKLM:\" + WindowsAiPolicy, "DisableAIDataAnalysis", 1),
                new DebloatRegistryChange(@"HKCU:\" + WindowsAiPolicy, "DisableAIDataAnalysis", 1)
            }
        };

        // Твики, которые Проводник подхватывает только после перезапуска (панель задач,
        // поиск, виджеты, меню «Пуск»).
        private static readonly HashSet<string> ExplorerRestartTweaks = new(StringComparer.OrdinalIgnoreCase)
        {
            "ads_start_recommendations", "ads_account_notifications", "ads_explorer_sync",
            "ads_search_web", "ads_search_highlights", "ads_widgets", "ads_news_feeds", "ads_copilot"
        };

        /// <summary>Подействует ли твик только после перезапуска Проводника.</summary>
        public static bool NeedsExplorerRestart(string tweakId) => ExplorerRestartTweaks.Contains(tweakId);

        /// <summary>Твик удаляет пакет Appx (встроенное приложение или Copilot).</summary>
        public static bool IsAppxTweak(string category, string tweakId) =>
            category == "app" || (category == "ads" && string.Equals(tweakId, CopilotAppId, StringComparison.OrdinalIgnoreCase));

        /// <summary>Все идентификаторы твиков с правками реестра — для проверок целостности таблиц.</summary>
        internal static IEnumerable<(string Category, string Id, DebloatRegistryChange[] Changes)> AllRegistryTweaks() =>
            PrivacyRegistry.Select(p => ("privacy", p.Key, p.Value))
                .Concat(AdsRegistry.Select(p => ("ads", p.Key, p.Value)));

        /// <summary>Значения реестра, которые меняет твик; пусто, если он реестр не трогает.</summary>
        public static IReadOnlyList<DebloatRegistryChange> RegistryChangesOf(string category, string tweakId) => category switch
        {
            "privacy" when PrivacyRegistry.TryGetValue(tweakId, out var privacy) => privacy,
            "ads" when AdsRegistry.TryGetValue(tweakId, out var ads) => ads,
            _ => Array.Empty<DebloatRegistryChange>()
        };

        /// <summary>Служба, которую твик останавливает и отключает; null, если служб он не трогает.</summary>
        public static string? ServiceOf(string category, string tweakId) => (category, tweakId) switch
        {
            ("privacy", "diag_track")       => "DiagTrack",
            ("service", "svc_diagtrack")    => "DiagTrack",
            ("service", "svc_sysmain")      => "SysMain",
            ("service", "svc_dmwappushsvc") => "dmwappushservice",
            _                               => null
        };

        /// <summary>
        /// Всё, что твик с таким идентификатором вправе менять, без оглядки на категорию.
        /// По этому списку откат отсеивает записи файла, которых в описании твика нет.
        /// </summary>
        public static (IReadOnlyList<DebloatRegistryChange> Registry, IReadOnlyList<string> Services) KnownChangesOf(string tweakId)
        {
            var services = new List<string>(2);
            foreach (string category in new[] { "privacy", "service" })
            {
                if (ServiceOf(category, tweakId) is { } service && !services.Contains(service))
                    services.Add(service);
            }
            // Идентификаторы твиков уникальны во всех таблицах (это проверяет юнит-тест),
            // поэтому твик находится не более чем в одной из них.
            var registry = RegistryChangesOf("privacy", tweakId);
            if (registry.Count == 0) registry = RegistryChangesOf("ads", tweakId);
            return (registry, services);
        }

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

        /// <summary>
        /// Твик группы «Реклама и ИИ»: правки реестра по таблице, удаление пакета Copilot
        /// или удаление OneDrive (со своей проверкой папок — см. <see cref="OneDriveRemovalService"/>).
        /// </summary>
        public static async Task<bool> ApplyAdsTweakAsync(string tweakId, CancellationToken ct = default)
        {
            if (AdsRegistry.TryGetValue(tweakId, out var changes))
            {
                bool all = true;
                foreach (var change in changes)
                    all &= await SetReg(change.Path, change.Name, change.Value, ct);
                return all;
            }

            if (string.Equals(tweakId, CopilotAppId, StringComparison.OrdinalIgnoreCase))
                return await RemoveAppxAsync(CopilotAppId, ct);

            if (tweakId == OneDriveId)
            {
                var result = await OneDriveRemovalService.UninstallAsync(ct);
                if (result.Message is { Length: > 0 } message)
                    AppLogger.Write($"[Деблоатер] OneDrive: {message}");
                return result.Ok;
            }

            AppLogger.Write($"[Деблоатер] Неизвестный tweakId: {tweakId}");
            return false;
        }

        /// <summary>
        /// Перезапускает Проводник текущего сеанса, чтобы он перечитал настройки панели
        /// задач, поиска и «Пуска». Новый Проводник поднимает сама Windows (Winlogon,
        /// AutoRestartShell) — с правами пользователя, а не администратора. Только если
        /// за 10 секунд он не появился, запускается вручную.
        /// </summary>
        public static async Task<bool> RestartExplorerAsync()
        {
            try
            {
                int session = Process.GetCurrentProcess().SessionId;
                foreach (var p in Process.GetProcessesByName("explorer"))
                {
                    using (p)
                    {
                        try { if (p.SessionId == session) p.Kill(); }
                        catch (Exception ex) { AppLogger.Write($"[Деблоатер] Проводник не остановлен: {ex.Message}"); }
                    }
                }

                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(500);
                    if (ExplorerRunningInSession(session)) return true;
                }

                AppLogger.Write("[Деблоатер] Проводник не перезапустился сам за 10 с — запускаю вручную");
                Process.Start(new ProcessStartInfo(TrustedExecutablePaths.ExplorerExe) { UseShellExecute = false })?.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Деблоатер] Ошибка перезапуска Проводника: {ex.Message}");
                return false;
            }
        }

        private static bool ExplorerRunningInSession(int session)
        {
            bool found = false;
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                using (p)
                {
                    try { if (!p.HasExited && p.SessionId == session) found = true; }
                    catch { }
                }
            }
            return found;
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
