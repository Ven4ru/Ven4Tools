using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    public static class PackageManagerService
    {
        // ── Chocolatey ────────────────────────────────────────────────────────────

        // Кэш на сессию: IsChocoInstalledAsync дёргается на каждый ввод в поиске
        // (SearchChocoAsync) — без кэша это блокирующий процесс на каждое нажатие клавиши.
        // Сбрасывается после InstallChocoAsync, чтобы отразить свежую установку.
        private static bool? _cachedChocoInstalled;

        // 10 минут — тот же порядок величины, что уже используется в проекте для
        // аналогичных операций (Ven4Tools.Launcher.InstallChocoAsync, LauncherUpdateService).
        // Меньше внутреннего таймаута самого Chocolatey (2700 сек / 45 мин — см.
        // commandExecutionTimeoutSeconds в chocolatey.log): раньше при зависании
        // скрипта установки пакета (подтверждено живьём в chocolatey.log за
        // 2026-07-05 — hwmonitor и cpu-z зависли на 45 минут каждый на скачивании с
        // download.cpuid.com) вызывающий код ждал молча ровно столько, сколько ждал
        // сам Chocolatey — никакого собственного дедлайна не было, только внешняя
        // отмена пользователем. Теперь зависание всплывает как явная ошибка
        // заметно раньше, чем через 45 минут молчания.
        private static readonly TimeSpan InstallOperationTimeout = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Готовит пару токенов для ожидания процесса с собственным дедлайном поверх
        /// внешнего токена отмены. Выделено в чистую функцию без зависимости от
        /// Process — тестируется без реального choco.exe.
        /// </summary>
        internal static (CancellationTokenSource TimeoutCts, CancellationTokenSource LinkedCts) CreateInstallTimeoutTokens(
            CancellationToken externalToken, TimeSpan timeout)
        {
            var timeoutCts = new CancellationTokenSource(timeout);
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken, timeoutCts.Token);
            return (timeoutCts, linkedCts);
        }

        /// <summary>
        /// Отличает истечение внутреннего таймаута от отмены пользователем — после
        /// того как linkedCts из <see cref="CreateInstallTimeoutTokens"/> сработал,
        /// вызывающий код должен показать разные сообщения («таймаут» — это не
        /// «отменено пользователем»).
        /// </summary>
        internal static bool IsTimeoutNotCancellation(CancellationTokenSource timeoutCts, CancellationToken externalToken)
            => timeoutCts.IsCancellationRequested && !externalToken.IsCancellationRequested;

        public static async Task<bool> IsChocoInstalledAsync()
        {
            if (_cachedChocoInstalled.HasValue) return _cachedChocoInstalled.Value;
            bool result = await Task.Run(() =>
            {
                try
                {
                    var chocoPath = TrustedExecutablePaths.ResolveChocolatey();
                    if (chocoPath == null) return false;
                    var psi = new ProcessStartInfo(chocoPath, "--version")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var p = Process.Start(psi);
                    if (p == null) return false;
                    // Читаем потоки в фоне — иначе дедлок если буфер переполнится
                    var outTask = Task.Run(() => p.StandardOutput.ReadToEnd());
                    var errTask = Task.Run(() => p.StandardError.ReadToEnd());
                    bool exited = p.WaitForExit(5000);
                    if (!exited) { try { p.Kill(); } catch { } }
                    return exited && p.ExitCode == 0;
                }
                catch { return false; }
            });
            _cachedChocoInstalled = result;
            return result;
        }

        public static async Task<bool> InstallChocoAsync(CancellationToken token, Action<string>? log = null)
        {
            log?.Invoke("📦 Установка Chocolatey...");
            try
            {
                // Официальный установочный скрипт Chocolatey. URL — HTTPS-литерал на
                // конкретный домен (нет пользовательского ввода — валидировать нечего),
                // TLS 1.2 включается принудительно. Хеш скрипта намеренно НЕ пиннится:
                // это upstream-механизм, скрипт регулярно меняется на стороне Chocolatey.
                // Логируем источник ради прозрачности того, что будет исполнено через iex.
                const string chocoInstallScriptUrl = "https://community.chocolatey.org/install.ps1";
                log?.Invoke($"⤓ Источник (iex): {chocoInstallScriptUrl}");
                string cmd =
                    "Set-ExecutionPolicy Bypass -Scope Process -Force; " +
                    "[System.Net.ServicePointManager]::SecurityProtocol = " +
                    "[System.Net.ServicePointManager]::SecurityProtocol -bor 3072; " +
                    $"iex ((New-Object System.Net.WebClient).DownloadString('{chocoInstallScriptUrl}'))";

                var psi = new ProcessStartInfo
                {
                    FileName = TrustedExecutablePaths.PowerShellExe,
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{cmd}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;

                // Раньше здесь не было вообще НИКАКОГО ограничения по времени — ни
                // внутреннего таймаута, ни даже внешнего CancellationToken (метод не
                // принимал его параметром): зависший официальный install-скрипт (сеть,
                // неожиданный промпт) ждал бы бесконечно, и пользователь не мог бы
                // прервать это иначе как убив весь процесс Ven4Tools. См.
                // InstallOperationTimeout выше — тот же дедлайн, что и для установки
                // отдельных пакетов через choco (RunChocoInstallAsync).
                var (timeoutCts, linkedCts) = CreateInstallTimeoutTokens(token, InstallOperationTimeout);
                using (timeoutCts)
                using (linkedCts)
                {
                    var stdoutTask = p.StandardOutput.ReadToEndAsync(linkedCts.Token);
                    var stderrTask = p.StandardError.ReadToEndAsync(linkedCts.Token);
                    try
                    {
                        await p.WaitForExitAsync(linkedCts.Token);
                        await Task.WhenAll(stdoutTask, stderrTask);
                    }
                    catch (OperationCanceledException)
                    {
                        try { p.Kill(entireProcessTree: true); } catch { }
                        if (IsTimeoutNotCancellation(timeoutCts, token))
                        {
                            log?.Invoke($"⏱️ Установка Chocolatey превысила таймаут {(int)InstallOperationTimeout.TotalMinutes} мин — прервана");
                            return false;
                        }
                        throw;
                    }
                }

                _cachedChocoInstalled = null; // сброс кэша — версия могла измениться
                bool ok = await IsChocoInstalledAsync();
                log?.Invoke(ok ? "✅ Chocolatey установлен" : "⚠️ Chocolatey не найден после установки");
                return ok;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log?.Invoke($"❌ Ошибка установки Chocolatey: {ex.Message}");
                return false;
            }
        }

        public static async Task<bool> RunChocoInstallAsync(
            string packageId, CancellationToken token, Action<string>? log = null)
        {
            if (!CommandLineGuard.ValidateId(packageId))
            {
                log?.Invoke($"❌ Choco: недопустимый идентификатор пакета «{packageId}»");
                return false;
            }
            log?.Invoke($"🍫 Choco: установка {packageId}...");
            var chocoExe = TrustedExecutablePaths.ResolveChocolatey();
            if (chocoExe == null)
            {
                log?.Invoke("❌ Choco: исполняемый файл не найден по доверенному пути");
                return false;
            }
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = chocoExe,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };
                // ArgumentList вместо интерполяции: .NET экранирует каждый токен —
                // единый с остальным кодом паттерн (packageId уже прошёл ValidateId).
                psi.ArgumentList.Add("install");
                psi.ArgumentList.Add(packageId);
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("--no-progress");
                psi.ArgumentList.Add("--limit-output");
                using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) log?.Invoke($"  choco: {e.Data}"); };
                p.ErrorDataReceived  += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) log?.Invoke($"  choco: {e.Data}"); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                // Раньше цикл ожидания полагался ТОЛЬКО на внешний token (отмена
                // пользователем кнопкой) — никакого собственного дедлайна не было.
                // Подтверждено живьём в chocolatey.log (2026-07-05): hwmonitor и cpu-z
                // зависли на скачивании с download.cpuid.com и провисели ровно 45 минут
                // (внутренний таймаут самого Chocolatey, commandExecutionTimeoutSeconds),
                // прежде чем сам Chocolatey их прервал — всё это время наш процесс ждал
                // молча. InstallOperationTimeout меньше 45 минут: зависание теперь
                // всплывает пользователю заметно раньше.
                var (timeoutCts, linkedCts) = CreateInstallTimeoutTokens(token, InstallOperationTimeout);
                using (timeoutCts)
                using (linkedCts)
                {
                    try
                    {
                        await p.WaitForExitAsync(linkedCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        try { p.Kill(entireProcessTree: true); } catch { }
                        if (IsTimeoutNotCancellation(timeoutCts, token))
                        {
                            log?.Invoke($"⏱️ Choco: установка «{packageId}» превысила таймаут {(int)InstallOperationTimeout.TotalMinutes} мин — прервана");
                            return false;
                        }
                        token.ThrowIfCancellationRequested();
                    }
                }
                return p.ExitCode == 0;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log?.Invoke($"❌ Choco: {ex.Message}"); return false; }
        }

        // ── Search ────────────────────────────────────────────────────────────────

        public static async Task<List<(string Id, string Name, string Version)>> SearchChocoAsync(
            string query, CancellationToken token = default)
        {
            var results = new List<(string, string, string)>();
            query = CommandLineGuard.SanitizeQuery(query);
            if (string.IsNullOrEmpty(query) || !await IsChocoInstalledAsync()) return results;
            var chocoExe = TrustedExecutablePaths.ResolveChocolatey();
            if (chocoExe == null) return results;
            try
            {
                var psi = new ProcessStartInfo(chocoExe)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute = false, CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding  = System.Text.Encoding.UTF8
                };
                // ArgumentList вместо интерполяции: .NET экранирует каждый токен —
                // единый с остальным кодом паттерн (query уже прошёл SanitizeQuery).
                psi.ArgumentList.Add("search");
                psi.ArgumentList.Add(query);
                psi.ArgumentList.Add("--limit-output");
                psi.ArgumentList.Add("--page-size");
                psi.ArgumentList.Add("8");
                using var p = Process.Start(psi);
                if (p == null) return results;
                // Токен передан в чтение (не только в WaitForExitAsync) — иначе зависший
                // choco мог бы задержать возврат дольше, чем ожидает вызывающий код.
                var errTask = p.StandardError.ReadToEndAsync(token);
                string output = await p.StandardOutput.ReadToEndAsync(token);
                await p.WaitForExitAsync(token);
                await errTask;
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Trim().Split('|');
                    if (parts.Length >= 2)
                        results.Add((parts[0].Trim(), parts[0].Trim(), parts[1].Trim()));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Отмена вызывающей стороной — пробрасываем, а не превращаем в пустой
                // список: вызывающий код должен отличать «отменено» от «не найдено»
                // (см. тот же фикс в WingetService).
                throw;
            }
            catch (Exception ex) { AppLogger.Write($"[PackageManagerService] Поиск в Chocolatey: {ex.Message}"); }
            return results;
        }

    }
}
