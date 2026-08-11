using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Helpers;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public partial class InstallationService : IDisposable
    {
        // Общий семафор установки на всё приложение. Установка запускается из трёх мест
        // (каталог, пины в MainWindow, переустановка из истории) — без единого ограничения
        // можно запустить параллельные msiexec, что вызывает ошибку Windows Installer 1618.
        public static readonly SemaphoreSlim InstallSemaphore = new SemaphoreSlim(1, 1);

        // Используется для явной блокировки кнопок вместо тихого ожидания семафора —
        // и каталогом/историей, и Windows Update (Task 8), т.к. оба используют
        // общую MSI-подсистему и не должны ставить/удалять параллельно.
        public static bool IsBusy => InstallSemaphore.CurrentCount == 0;

        // Один общий HttpClient на приложение: пересоздание на каждый инстанс
        // приводит к socket exhaustion (рекомендация MS).
        // Без глобального таймаута: HttpClient.Timeout ограничивает всё тело ответа,
        // и загрузки больших установщиков (100+ МБ) обрывались через 30 секунд.
        // Таймаут на получение заголовков задаётся per-request через CancellationTokenSource.
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestHeaders = { { "User-Agent", "Ven4Tools" } }
        };
        private readonly string _logPath;
        private readonly object _logLock = new object();

        // Сколько журналов установки хранить. Каждый запуск установки создаёт свой файл,
        // и без ограничения папка logs росла бесконечно: единственной уборкой была
        // ручная кнопка «Очистить логи». Остальные локальные журналы проекта давно
        // ограничены (app.log — ротация по 1 МБ, история установок и журнал сбоев —
        // по 100 записей), этот оставался единственным без предела.
        private const int MaxInstallLogFiles = 30;

        public InstallationService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logsFolder = Path.Combine(appData, "Ven4Tools", "logs");
            Directory.CreateDirectory(logsFolder);

            TrimOldInstallLogs(logsFolder);

            _logPath = Path.Combine(logsFolder, $"install_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
        }

        /// <summary>
        /// Удаляет самые старые журналы установки, оставляя не более
        /// <see cref="MaxInstallLogFiles"/> последних. Порядок — по имени файла:
        /// оно начинается с отсортированной метки времени, поэтому лексикографическая
        /// сортировка совпадает с хронологической и не зависит от времени файловой
        /// системы (его меняет копирование папки). Best-effort: сбой уборки не должен
        /// мешать установке.
        /// </summary>
        private static void TrimOldInstallLogs(string logsFolder)
        {
            try
            {
                // Подменённый ссылкой каталог не трогаем — тот же guard, что у Log()
                // и AppLogger: клиент работает elevated, а папка доступна на запись
                // обычному процессу пользователя.
                if (PathHelper.IsReparsePoint(logsFolder)) return;

                var files = Directory.GetFiles(logsFolder, "install_*.log");
                if (files.Length <= MaxInstallLogFiles) return;

                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < files.Length - MaxInstallLogFiles; i++)
                {
                    try { File.Delete(files[i]); } catch { }
                }
            }
            catch { }
        }

        // Диспетчер установки: последовательно пробует стратегии в порядке приоритета
        // (локальный файл → офлайн-кэш → строгий офлайн-abort → цепочка источников
        // winget/choco/direct). Сама логика каждой стратегии вынесена в отдельные
        // приватные методы; поведение идентично прежней монолитной версии.
        public async Task<(bool Success, string Message, AppInstallProgress Progress)> InstallAppAsync(
            AppInfo app, string[] wingetSources, CancellationToken token,
            IProgress<AppInstallProgress> progress, string installDrive, string? version = null,
            Func<string, Task<bool>>? confirmPmInstall = null)
        {
            var appProgress = new AppInstallProgress { AppId = app.Id, AppName = app.DisplayName };

            try
            {
                Log($"Начало установки: {app.DisplayName}");
                appProgress.Status = "Начинаем...";
                progress.Report(appProgress);

                token.ThrowIfCancellationRequested();

                // ── Baseline ДО установки: приложение уже стоит? какая версия? ──
                // Опорная точка для сверки по факту после установки (см.
                // InstallOutcomeEvaluator) — без неё нельзя отличить «поставили
                // впервые» от «уже стояло, ничего не изменилось» (AlreadyUpToDate).
                // Тот же ключ (AlternativeId ?? Id), что уже использует
                // CatalogViewModel.UpdateInstalledStatusAsync для бейджа «установлено»
                // на карточках — единая точка правды по всему клиенту.
                string outcomeCheckId = !string.IsNullOrEmpty(app.AlternativeId) ? app.AlternativeId! : app.Id;
                var baseline = await CaptureInstalledBaselineAsync(outcomeCheckId);

                // ── Local installer (drag-drop) ────────────────────────────────
                if (!string.IsNullOrEmpty(app.LocalInstallerPath) && File.Exists(app.LocalInstallerPath))
                    return await InstallFromLocalAsync(app, appProgress, progress, installDrive, outcomeCheckId, baseline, token);

                // ── Offline cache ──────────────────────────────────────────────
                var cacheResult = await InstallFromCacheAsync(app, appProgress, progress, installDrive, outcomeCheckId, baseline, token);
                if (cacheResult != null) return cacheResult.Value;

                // ── Строгий офлайн без кэша — прекращаем ────────────────────────
                if (OfflineService.IsOffline)
                {
                    appProgress.Status = "❌ Нет в кэше (офлайн режим)";
                    appProgress.Phase = InstallPhase.Error;
                    progress.Report(appProgress);
                    Log($"⚠️ {app.DisplayName}: офлайн режим, установщик не кэширован");
                    return (false, "Офлайн режим — нет кэша", appProgress);
                }

                // ── Цепочка источников (winget/choco/direct) ───────────────────
                return await InstallFromSourcesAsync(
                    app, wingetSources, appProgress, progress, installDrive, version, confirmPmInstall,
                    outcomeCheckId, baseline, token);
            }
            catch (OperationCanceledException)
            {
                appProgress.Status = "⏹️ Отменено";
                appProgress.Phase = InstallPhase.Error;
                appProgress.IsIndeterminate = false;
                progress.Report(appProgress);
                Log($"⏹️ {app.DisplayName} отменено");
                throw;
            }
            catch (Exception ex)
            {
                appProgress.Status = "❌ Ошибка";
                appProgress.Phase = InstallPhase.Error;
                appProgress.IsIndeterminate = false;
                progress.Report(appProgress);
                Log($"❌ {app.DisplayName}: {ex.Message}");
                return (false, ex.Message, appProgress);
            }
        }

        /// <summary>
        /// Итог попытки установки из ОДНОГО источника цепочки.
        /// <para><c>Result</c> — терминальный итог: если не null, цепочка источников
        /// на этом заканчивается (источник отработал).</para>
        /// <para><c>FailureDetail</c> — читаемая причина неудачи этого источника
        /// (расшифровка кода выхода), чтобы при исчерпании всей цепочки показать
        /// пользователю не «Код выхода — ошибка», а последнюю внятную причину.</para>
        /// Отдельным полем, а не <c>ref</c>-параметром: C# запрещает <c>ref</c>/<c>out</c>
        /// у async-методов (CS1988).
        /// </summary>
        private readonly struct SourceAttempt
        {
            public (bool Success, string Message, AppInstallProgress Progress)? Result { get; init; }
            public string? FailureDetail { get; init; }

            public static SourceAttempt Finished((bool Success, string Message, AppInstallProgress Progress) result)
                => new() { Result = result };

            public static SourceAttempt Failed(string? failureDetail)
                => new() { FailureDetail = failureDetail };
        }

        // Приводит итог источника без расшифровки кодов (прямая загрузка) к общему
        // виду цепочки: терминальный итог либо «источник не подошёл, идём дальше».
        private static SourceAttempt WrapDirectAttempt(
            (bool Success, string Message, AppInstallProgress Progress)? result)
            => result != null ? SourceAttempt.Finished(result.Value) : SourceAttempt.Failed(null);

        // ── Стратегия 3: цепочка источников (winget → choco → direct) ──────────
        private async Task<(bool Success, string Message, AppInstallProgress Progress)> InstallFromSourcesAsync(
            AppInfo app, string[] wingetSources, AppInstallProgress appProgress,
            IProgress<AppInstallProgress> progress, string installDrive, string? version,
            Func<string, Task<bool>>? confirmPmInstall, string outcomeCheckId, InstalledBaseline baseline,
            CancellationToken token)
        {
            string primaryId = !string.IsNullOrEmpty(app.AlternativeId) ? app.AlternativeId : app.Id;

            // ID может приходить из ручного ввода (пользовательские приложения,
            // альтернативные источники) — проверяем всегда перед подстановкой в
            // командную строку winget/choco, чтобы исключить внедрение аргументов.
            if (!CommandLineGuard.ValidateId(primaryId))
            {
                appProgress.Status = "❌ Недопустимый идентификатор пакета";
                appProgress.Phase = InstallPhase.Error;
                appProgress.IsIndeterminate = false;
                progress.Report(appProgress);
                Log($"❌ {app.DisplayName}: недопустимый ID «{primaryId}» — установка отменена");
                InstallFailureService.Append(app.DisplayName, app.Id, "validation", $"Недопустимый ID «{primaryId}»");
                return (false, "Недопустимый идентификатор пакета", appProgress);
            }

            var sourceOrder  = SourceOrderService.GetOrderForCategory(app.CategoryString);
            Log($"🔀 Порядок источников для «{app.DisplayName}»: {string.Join(" → ", sourceOrder)}");

            // Последняя внятная причина неудачи из пройденных источников —
            // попадёт в блок «Не установлено» вместо голого «Код выхода — ошибка».
            string? lastFailureDetail = null;

            foreach (var srcId in sourceOrder)
            {
                token.ThrowIfCancellationRequested();

                var attempt = srcId switch
                {
                    SourceOrderSettings.Winget => await InstallFromWingetAsync(
                        app, primaryId, wingetSources, appProgress, progress, installDrive, version,
                        outcomeCheckId, baseline, token),
                    SourceOrderSettings.Choco => await InstallFromChocoAsync(
                        app, appProgress, progress, confirmPmInstall, outcomeCheckId, baseline, token),
                    // Прямая загрузка со своей MSI/EXE-спецификой расшифровки кодов не
                    // даёт — оборачиваем её итог как есть, без причины неудачи.
                    SourceOrderSettings.Direct => WrapDirectAttempt(await InstallFromDirectDownloadAsync(
                        app, primaryId, appProgress, progress, installDrive, outcomeCheckId, baseline, token)),
                    _ => default
                };
                if (attempt.FailureDetail != null) lastFailureDetail = attempt.FailureDetail;
                if (attempt.Result != null) return attempt.Result.Value;
            }

            // Все источники исчерпаны — терминальная неудача цепочки. Сверяем с
            // фактическим состоянием системы на случай, если один из источников
            // (например choco) на самом деле справился, но был ошибочно распознан
            // как неудача (см. ReportInstallOutcomeAsync — честная коррекция).
            string finalDetail = lastFailureDetail != null
                ? $"все источники исчерпаны — последняя причина: {lastFailureDetail}"
                : "все источники исчерпаны";
            return await ReportInstallOutcomeAsync(app, appProgress, progress, outcomeCheckId, baseline,
                false, false, "all-sources", token, finalDetail);
        }

        // ── Общий запуск elevated-установщика ──────────────────────────────────
        // Стартует процесс с Verb=runas, ждёт завершения (убивая всё дерево при отмене)
        // и интерпретирует код выхода. Возвращает null, если процесс не запустился;
        // иначе Ok (0 или 3010) и Reboot (3010 = требуется перезагрузка) плюс код выхода.
        private static async Task<(bool Ok, bool Reboot, int ExitCode)?> RunElevatedInstallerAsync(
            ProcessStartInfo psi, CancellationToken token, Action<int>? onStarted = null)
        {
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            onStarted?.Invoke(proc.Id);

            while (!proc.HasExited)
            {
                if (token.IsCancellationRequested) { try { proc.Kill(entireProcessTree: true); } catch { } token.ThrowIfCancellationRequested(); }
                await Task.Delay(100, token);
            }

            // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED — считаем успехом
            return (proc.ExitCode == 0 || proc.ExitCode == 3010, proc.ExitCode == 3010, proc.ExitCode);
        }

        // ── Помощники для выбора диска установки ───────────────────────────────

        private static string GetSystemDriveRoot()
            => Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

        /// <summary>
        /// true, если пользователь выбрал диск, отличный от системного.
        /// Только в этом случае имеет смысл переопределять путь установки —
        /// для системного диска поведение по умолчанию и так корректно.
        /// </summary>
        private static bool IsNonSystemDrive(string? installDrive)
        {
            if (string.IsNullOrWhiteSpace(installDrive)) return false;
            string sys = GetSystemDriveRoot().TrimEnd('\\', '/').ToUpperInvariant();
            string sel = installDrive.TrimEnd('\\', '/').ToUpperInvariant();
            return sel.Length >= 2 && sel != sys;
        }

        /// <summary>Базовая папка «{диск}:\Program Files».</summary>
        private static string ProgramFilesOn(string installDrive)
        {
            string root = installDrive.TrimEnd('\\', '/');
            return $"{root}\\Program Files";
        }

        /// <summary>Целевая папка установки для конкретного приложения (best-effort).</summary>
        private static string TargetFolderFor(string installDrive, string appName)
        {
            // Замена недопустимых символов (не удаление) — иначе два разных
            // названия приложений могут схлопнуться в одну и ту же папку
            // (например "A/B" и "AB" раньше давали одинаковый результат "AB").
            string safe = PathHelper.SanitizeFileNameComponent(appName ?? "App");
            if (string.IsNullOrWhiteSpace(safe)) safe = "App";
            return Path.Combine(ProgramFilesOn(installDrive), safe);
        }

        /// <summary>
        /// Best-effort аргумент пути установки для прямых установщиков.
        /// Применяется только при выборе несистемного диска, чтобы не ломать
        /// штатные тихие установки на системный диск.
        /// </summary>
        private static string BuildInstallerLocationArg(bool isMsi, string? installDrive, string appName)
        {
            if (!IsNonSystemDrive(installDrive)) return "";
            if (!isMsi) return ""; // формат EXE-установщика неизвестен — не вмешиваемся
            string target = TargetFolderFor(installDrive!, appName);
            return $" INSTALLDIR=\"{target}\"";
        }

        private void Log(string message)
        {
            try
            {
                lock (_logLock)
                {
                    // Тот же guard, что у AppLogger: журнал установки лежит в том же
                    // доступном пользователю дереве %LocalAppData%\Ven4Tools, а клиент
                    // работает elevated — без проверки подмена каталога/файла junction'ом
                    // перенаправила бы elevated-дозапись в защищённую цель. Раньше guard
                    // стоял только у AppLogger, хотя оба пишут в одно и то же дерево.
                    if (PathHelper.IsReparsePoint(Path.GetDirectoryName(_logPath)!) ||
                        PathHelper.IsReparsePoint(_logPath))
                        return;

                    File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss} - {message}\n");
                }
            }
            catch { }
        }

        public void Dispose()
        {
            // HttpClient общий (static) — живёт всё время работы приложения, не освобождается здесь.
        }
    }
}
