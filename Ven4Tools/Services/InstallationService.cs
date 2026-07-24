using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Helpers;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Services
{
    public class InstallationService : IDisposable
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

        public InstallationService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logsFolder = Path.Combine(appData, "Ven4Tools", "logs");
            Directory.CreateDirectory(logsFolder);

            _logPath = Path.Combine(logsFolder, $"install_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
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

                // ── Local installer (drag-drop) ────────────────────────────────
                if (!string.IsNullOrEmpty(app.LocalInstallerPath) && File.Exists(app.LocalInstallerPath))
                    return await InstallFromLocalAsync(app, appProgress, progress, installDrive, token);

                // ── Offline cache ──────────────────────────────────────────────
                var cacheResult = await InstallFromCacheAsync(app, appProgress, progress, installDrive, token);
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
                    app, wingetSources, appProgress, progress, installDrive, version, confirmPmInstall, token);
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

        // ── Стратегия 1: локальный установщик (drag-drop) ──────────────────────
        private async Task<(bool Success, string Message, AppInstallProgress Progress)> InstallFromLocalAsync(
            AppInfo app, AppInstallProgress appProgress, IProgress<AppInstallProgress> progress,
            string installDrive, CancellationToken token)
        {
            // Файл уже на диске (drag-drop) — фазы «Загрузка» здесь нет вообще,
            // сразу Installing. Гранулярного прогресса самого установщика нет
            // (elevated чёрный ящик) — честно показываем IsIndeterminate, а не
            // выдумываем проценты.
            appProgress.Status = "📂 Локальный установщик...";
            appProgress.Phase = InstallPhase.Installing;
            appProgress.IsIndeterminate = true;
            appProgress.Percentage = 0;
            progress.Report(appProgress);
            Log($"📂 {app.DisplayName}: локальный файл {app.LocalInstallerPath}");

            // apps.json (LocalAppData) доступен на запись любому не-elevated процессу
            // пользователя — перед runas-запуском сверяем хеш, зафиксированный при
            // добавлении (LocalInstallerDialog), чтобы обнаружить подмену файла/пути.
            // Fail-closed: отсутствующий/невалидный Sha256 — это НЕ "проверка не нужна",
            // а сигнал, что запись могла быть подделана (LocalInstallerDialog всегда
            // считает и сохраняет хеш при легитимном добавлении). Раньше здесь была
            // конъюнкция "если хеш есть И не совпадает" — при пустом хеше она молча
            // пропускала проверку целиком (см. HIGH-находку аудита 2026-07-13).
            if (!HashHelper.HasExpectedHash(app.Sha256))
            {
                appProgress.Status = "❌ Нет SHA256 для локального установщика — запуск отклонён";
                appProgress.Phase = InstallPhase.Error;
                appProgress.IsIndeterminate = false;
                progress.Report(appProgress);
                Log($"❌ {app.DisplayName}: локальный установщик без SHA256 — запуск отклонён (fail-closed)");
                InstallFailureService.Append(app.DisplayName, app.Id, "local", "Нет SHA256 — установка локального файла требует зафиксированного хеша");
                return (false, "Нет SHA256 для локального установщика", appProgress);
            }
            bool isMsi = app.LocalInstallerPath!.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
            string locArgLocal = BuildInstallerLocationArg(isMsi, installDrive, app.DisplayName);
            // SilentArgs может приходить извне (каталог, ручной ввод) — валидируем
            // перед подстановкой в elevated-процесс, как в ветке прямой загрузки.
            string silentArgsLocal = app.SilentArgs;
            if (!CommandLineGuard.ValidateSilentArgs(silentArgsLocal))
            {
                AppLogger.Write($"[InstallationService] ⚠ SilentArgs содержит недопустимые символы для {app.DisplayName} — использую /S");
                silentArgsLocal = "/S";
            }
            if (string.IsNullOrWhiteSpace(silentArgsLocal))
                silentArgsLocal = "/S";
            // Держим файл открытым с FileShare.Read НЕПРЕРЫВНО от проверки хеша до
            // завершения установки — верификация читает из уже открытого хендла, а не
            // из отдельного временного, который закрывался бы до открытия защитного
            // (иначе между закрытием проверочного хендла и открытием защитного остаётся
            // окно для подмены файла — TOCTOU). FileShare.Read позволяет самому
            // установщику/msiexec читать файл на исполнение, но блокирует запись.
            // Зеркалирует защиту лаунчера (DownloadVerifyAndRunElevatedAsync).
            (bool Ok, bool Reboot, int ExitCode)? run;
            using (var verifiedStream = new FileStream(app.LocalInstallerPath!, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (!await HashHelper.VerifyHashAsync(verifiedStream, app.Sha256!))
                {
                    appProgress.Status = "❌ Файл изменён с момента добавления";
                    appProgress.Phase = InstallPhase.Error;
                    appProgress.IsIndeterminate = false;
                    progress.Report(appProgress);
                    Log($"❌ {app.DisplayName}: SHA256 локального файла не совпадает с зафиксированным при добавлении");
                    InstallFailureService.Append(app.DisplayName, app.Id, "local", "SHA256 не совпадает — файл изменён с момента добавления");
                    return (false, "Файл изменён с момента добавления", appProgress);
                }

                var psiLocal = new ProcessStartInfo
                {
                    FileName        = isMsi ? TrustedExecutablePaths.MsiExec : app.LocalInstallerPath!,
                    Arguments       = isMsi
                                      ? $"/i \"{app.LocalInstallerPath}\" /quiet /norestart{locArgLocal}"
                                      : silentArgsLocal + locArgLocal,
                    UseShellExecute = true,
                    Verb            = "runas",
                    WindowStyle     = ProcessWindowStyle.Hidden
                };

                run = await RunElevatedInstallerAsync(psiLocal, token);
            }
            if (run == null)
            {
                appProgress.Status = "❌ Не удалось запустить установщик";
                appProgress.Phase = InstallPhase.Error;
                appProgress.IsIndeterminate = false;
                progress.Report(appProgress);
                Log($"❌ {app.DisplayName}: Process.Start вернул null");
                InstallFailureService.Append(app.DisplayName, app.Id, "local", "Process.Start вернул null");
                return (false, "Не удалось запустить установщик", appProgress);
            }

            if (run.Value.Ok)
                return await ReportInstallSuccessAsync(app, appProgress, progress, run.Value.Reboot, "local",
                    "✅ Установлено (локальный файл)",
                    $"✅ {app.DisplayName} — локальный установщик",
                    $"⚠ Установлено. Требуется перезагрузка. {app.DisplayName} — локальный установщик",
                    "Установлено из локального файла",
                    "Установлено (требуется перезагрузка)");

            appProgress.Status = "❌ Ошибка локального установщика";
            appProgress.Phase = InstallPhase.Error;
            appProgress.IsIndeterminate = false;
            progress.Report(appProgress);
            Log($"❌ {app.DisplayName}: локальный установщик завершился с кодом {run.Value.ExitCode}");
            InstallFailureService.Append(app.DisplayName, app.Id, "local", $"Код выхода {run.Value.ExitCode}");
            return (false, "Ошибка локального установщика", appProgress);
        }

        // ── Стратегия 2: офлайн-кэш ────────────────────────────────────────────
        // Возвращает null, если кэша нет / хеш не сошёлся / установка из кэша не
        // удалась — в этих случаях диспетчер продолжает следующими стратегиями.
        private async Task<(bool Success, string Message, AppInstallProgress Progress)?> InstallFromCacheAsync(
            AppInfo app, AppInstallProgress appProgress, IProgress<AppInstallProgress> progress,
            string installDrive, CancellationToken token)
        {
            string? cachedPath = OfflineService.GetCachedInstallerPath(app.Id);
            if (cachedPath == null) return null;

            // Fail-closed по аналогии с Direct-веткой: без SHA256 в каталоге
            // кэшированный файл нечем верифицировать — elevated-запуск такого
            // файла небезопасен, даже если он лежит в собственном кэше приложения.
            if (!HashHelper.HasExpectedHash(app.Sha256))
            {
                Log($"⚠ Нет SHA256 в каталоге для {app.DisplayName} — кэш пропущен, пробую следующий источник");
                return null;
            }
            // Файл уже в офлайн-кэше на диске — фазы «Загрузка» нет, сразу Installing.
            // Гранулярного прогресса самого установщика нет — честно IsIndeterminate.
            appProgress.Status = "🔌 Из кэша...";
            appProgress.Phase = InstallPhase.Installing;
            appProgress.IsIndeterminate = true;
            appProgress.Percentage = 0;
            progress.Report(appProgress);

            bool cacheIsMsi = cachedPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
            string locArgCache = BuildInstallerLocationArg(cacheIsMsi, installDrive, app.DisplayName);
            // Держим кэшированный файл открытым с FileShare.Read НЕПРЕРЫВНО от
            // проверки хеша до завершения установки — верификация читает из уже
            // открытого хендла, а не из отдельного временного (иначе между закрытием
            // проверочного хендла и открытием защитного остаётся окно для подмены —
            // TOCTOU). Зеркалирует защиту лаунчера. Хендл закрывается до File.Delete
            // ниже (using завершается раньше) — на удаление это не влияет.
            bool hashOk;
            (bool Ok, bool Reboot, int ExitCode)? run = null;
            using (var verifiedStream = new FileStream(cachedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                hashOk = await HashHelper.VerifyHashAsync(verifiedStream, app.Sha256!);
                if (hashOk)
                {
                    var psiCache = new ProcessStartInfo
                    {
                        FileName       = cacheIsMsi ? TrustedExecutablePaths.MsiExec : cachedPath,
                        Arguments      = cacheIsMsi
                                         ? $"/i \"{cachedPath}\" /quiet /norestart{locArgCache}"
                                         : "/S /silent /quiet" + locArgCache,
                        UseShellExecute = true, Verb = "runas",
                        WindowStyle     = ProcessWindowStyle.Hidden
                    };

                    run = await RunElevatedInstallerAsync(psiCache, token);
                }
            }
            if (!hashOk)
            {
                Log($"❌ SHA256 mismatch в кэше: {app.DisplayName}, удаляю");
                try { File.Delete(cachedPath); } catch { }
                return null;
            }
            if (run is { Ok: true })
                return await ReportInstallSuccessAsync(app, appProgress, progress, run.Value.Reboot, "cache",
                    "✅ Установлено (кэш)",
                    $"✅ {app.DisplayName} установлено из офлайн-кэша",
                    $"⚠ Установлено. Требуется перезагрузка. {app.DisplayName} (офлайн-кэш)",
                    "Установлено из офлайн-кэша",
                    "Установлено из кэша (требуется перезагрузка)");

            return null;
        }

        // ── Стратегия 3: цепочка источников (winget → choco → direct) ──────────
        private async Task<(bool Success, string Message, AppInstallProgress Progress)> InstallFromSourcesAsync(
            AppInfo app, string[] wingetSources, AppInstallProgress appProgress,
            IProgress<AppInstallProgress> progress, string installDrive, string? version,
            Func<string, Task<bool>>? confirmPmInstall, CancellationToken token)
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

            foreach (var srcId in sourceOrder)
            {
                token.ThrowIfCancellationRequested();

                var result = srcId switch
                {
                    SourceOrderSettings.Winget => await InstallFromWingetAsync(
                        app, primaryId, wingetSources, appProgress, progress, installDrive, version, token),
                    SourceOrderSettings.Choco => await InstallFromChocoAsync(
                        app, appProgress, progress, confirmPmInstall, token),
                    SourceOrderSettings.Direct => await InstallFromDirectDownloadAsync(
                        app, primaryId, appProgress, progress, installDrive, token),
                    _ => null
                };
                if (result != null) return result.Value;
            }

            appProgress.Status = "❌ Ошибка";
            appProgress.Phase = InstallPhase.Error;
            appProgress.IsIndeterminate = false;
            progress.Report(appProgress);
            Log($"❌ {app.DisplayName} — все источники исчерпаны");
            InstallFailureService.Append(app.DisplayName, app.Id, "all-sources", "Все источники исчерпаны");
            return (false, "Не удалось установить", appProgress);
        }

        // ── Источник: Winget ───────────────────────────────────────────────────
        private async Task<(bool Success, string Message, AppInstallProgress Progress)?> InstallFromWingetAsync(
            AppInfo app, string primaryId, string[] wingetSources, AppInstallProgress appProgress,
            IProgress<AppInstallProgress> progress, string installDrive, string? version, CancellationToken token)
        {
            if (string.IsNullOrEmpty(primaryId) || primaryId.StartsWith("User.")) return null;
            foreach (var wsrc in wingetSources)
            {
                token.ThrowIfCancellationRequested();
                // Winget скачивает и ставит пакет как единый чёрный ящик: RunWingetAsync
                // только логирует построчный вывод, не парсит из него проценты (WingetRunner
                // сознательно отбрасывает строки прогресс-бара как шум, см. IsTableSeparator/
                // построчный фильтр). Локализованный вывод (без --locale en-US по правилам
                // проекта) делает матчинг "Downloading"/"Installing" по тексту хрупким —
                // поэтому честно показываем IsIndeterminate на весь процесс, а не
                // выдумываем разбивку на фазы, которой на самом деле не видно.
                appProgress.Status = $"📦 Winget ({wsrc})...";
                appProgress.Phase = InstallPhase.Installing;
                appProgress.IsIndeterminate = true;
                appProgress.Percentage = 0;
                progress.Report(appProgress);

                if (await RunWingetAsync(primaryId, wsrc, token, version, installDrive))
                {
                    appProgress.Status = "✅ Установлено (Winget)";
                    appProgress.Phase = InstallPhase.Done;
                    appProgress.IsIndeterminate = false;
                    appProgress.Percentage = 100;
                    progress.Report(appProgress);
                    Log($"✅ {app.DisplayName} — Winget ({wsrc}): {primaryId}");
                    if (ProfileService.Current.SaveInstallHistory)
                        await InstallHistoryService.Instance.TrackAsync(app.Id, app.DisplayName, "winget", app.CategoryString);
                    return (true, "Установлено через Winget", appProgress);
                }
            }
            return null;
        }

        // ── Источник: Chocolatey ───────────────────────────────────────────────
        private async Task<(bool Success, string Message, AppInstallProgress Progress)?> InstallFromChocoAsync(
            AppInfo app, AppInstallProgress appProgress, IProgress<AppInstallProgress> progress,
            Func<string, Task<bool>>? confirmPmInstall, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(app.ChocoId)) return null;
            // Как и Winget — единый чёрный ящик. RunChocoInstallAsync запускает choco
            // с --no-progress --limit-output и только логирует строки, без парсинга
            // процентов скачивания. Честный IsIndeterminate вместо выдуманной разбивки.
            appProgress.Status = "🍫 Chocolatey...";
            appProgress.Phase = InstallPhase.Installing;
            appProgress.IsIndeterminate = true;
            appProgress.Percentage = 0;
            progress.Report(appProgress);

            bool chocoOk = await PackageManagerService.IsChocoInstalledAsync()
                || (!token.IsCancellationRequested
                    && confirmPmInstall != null
                    && await confirmPmInstall("Chocolatey")
                    && await PackageManagerService.InstallChocoAsync(msg => Log(msg)));
            if (chocoOk && await PackageManagerService.RunChocoInstallAsync(app.ChocoId, token, msg => Log(msg)))
            {
                appProgress.Status = "✅ Установлено (Chocolatey)";
                appProgress.Phase = InstallPhase.Done;
                appProgress.IsIndeterminate = false;
                appProgress.Percentage = 100;
                progress.Report(appProgress);
                Log($"✅ {app.DisplayName} — Chocolatey: {app.ChocoId}");
                if (ProfileService.Current.SaveInstallHistory)
                    await InstallHistoryService.Instance.TrackAsync(app.Id, app.DisplayName, "choco", app.CategoryString);
                return (true, "Установлено через Chocolatey", appProgress);
            }
            return null;
        }

        // ── Источник: прямая загрузка ──────────────────────────────────────────
        private async Task<(bool Success, string Message, AppInstallProgress Progress)?> InstallFromDirectDownloadAsync(
            AppInfo app, string primaryId, AppInstallProgress appProgress,
            IProgress<AppInstallProgress> progress, string installDrive, CancellationToken token)
        {
            if (!app.InstallerUrls.Any()) return null;

            // Прямые ссылки без SHA256 в каталоге не выполняем:
            // скачанный установщик нечем верифицировать, запускать его небезопасно.
            if (!HashHelper.HasExpectedHash(app.Sha256))
            {
                appProgress.Status = "⚠ Нет SHA256 в каталоге — прямая ссылка пропущена";
                appProgress.Phase = InstallPhase.Error;
                appProgress.IsIndeterminate = false;
                progress.Report(appProgress);
                Log($"⚠ Прямая ссылка без SHA256 для {app.DisplayName} — источник пропущен, пробую следующий");
                AppLogger.Write($"[InstallationService] ⚠ Прямая ссылка без SHA256 для {app.DisplayName} — источник пропущен, продолжаю через winget");
                InstallFailureService.Append(app.DisplayName, app.Id, "direct", "В каталоге не указан SHA256");
                return null;
            }

            // Несовпадение SHA256 у нескольких зеркал — одна запись
            // об ошибке после перебора всех ссылок, а не по одной на URL.
            int hashMismatchCount = 0;
            foreach (var url in app.InstallerUrls)
            {
                token.ThrowIfCancellationRequested();

                if (!DownloadValidator.ValidateUrl(url))
                {
                    AppLogger.Write($"[InstallationService] ⚠ Пропущен небезопасный URL (не HTTPS): {url}");
                    continue;
                }

                // Начало реальной фазы «Загрузка» — перескалировано на полный 0-100%
                // диапазон (раньше эта фаза жила в промежутке 20-50% общей шкалы,
                // что вместе с фазой установки смотрелось как одна невнятная полоска).
                appProgress.Status = "📥 Скачивание...";
                appProgress.Phase = InstallPhase.Download;
                appProgress.IsIndeterminate = false;
                appProgress.Percentage = 0;
                progress.Report(appProgress);

                string urlExt = Path.GetExtension(new Uri(url).LocalPath).ToLowerInvariant();
                if (string.IsNullOrEmpty(urlExt) || (urlExt != ".exe" && urlExt != ".msi")) urlExt = ".exe";
                // primaryId (AlternativeId ?? app.Id) уже провалидирован выше,
                // но в ветке с AlternativeId сам app.Id остаётся непроверенным — а именно
                // он идёт в имя временного файла. Тот же ValidateId, иначе безопасный плейсхолдер.
                string idForTempFile = CommandLineGuard.ValidateId(app.Id) ? app.Id : "app";
                string tempFile = Path.Combine(Path.GetTempPath(), $"{idForTempFile}_{Guid.NewGuid()}{urlExt}");
                try
                {
                    // Таймаут 30 секунд только на установление соединения и заголовки;
                    // скачивание тела дополнительно ограничено sliding-таймаутом простоя
                    // (idleCts, ниже) — без него сервер, отдающий байты бесконечно медленно
                    // (или вовсе переставший отвечать после заголовков), вешал бы загрузку
                    // до ручной отмены пользователем.
                    using var headersCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    headersCts.CancelAfter(TimeSpan.FromSeconds(30));
                    long totalRead = 0;
                    using (var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headersCts.Token))
                    {
                        response.EnsureSuccessStatusCode();

                        if (!DownloadValidator.ValidateAfterRedirect(response))
                        {
                            AppLogger.Write($"[InstallationService] ⚠ Редирект на небезопасный хост: {response.RequestMessage?.RequestUri?.Host}");
                            throw new InvalidOperationException($"Редирект на небезопасный хост: {response.RequestMessage?.RequestUri?.Host}");
                        }

                        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                        // Сервер не отдал Content-Length — реальный процент скачивания
                        // посчитать нечем. Честно показываем IsIndeterminate вместо
                        // застрявшего на месте (или выдуманного) числа.
                        if (totalBytes <= 0)
                        {
                            appProgress.IsIndeterminate = true;
                            progress.Report(appProgress);
                        }
                        using var contentStream = await response.Content.ReadAsStreamAsync();
                        using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);
                        // Сбрасывается после каждого успешного чтения — таймаут на простой между
                        // байтами, а не на всю загрузку целиком (большие легитимные файлы не рвутся).
                        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        idleCts.CancelAfter(TimeSpan.FromSeconds(60));
                        var buf = new byte[8192];
                        int bytesRead;
                        while ((bytesRead = await contentStream.ReadAsync(buf, idleCts.Token)) > 0)
                        {
                            idleCts.CancelAfter(TimeSpan.FromSeconds(60));
                            token.ThrowIfCancellationRequested();
                            await fileStream.WriteAsync(buf, 0, bytesRead, token);
                            totalRead += bytesRead;
                            if (totalBytes > 0)
                            {
                                appProgress.Percentage = (int)((double)totalRead / totalBytes * 100);
                                progress.Report(appProgress);
                            }
                        }
                    }

                    double downloadedMb = Math.Round(totalRead / 1_048_576.0, 1);
                    AppLogger.Write($"📥 Загружен установщик — {downloadedMb} МБ: {Path.GetFileName(tempFile)}");

                    // Всё ещё часть фазы «Загрузка» (файл уже получен, проверяем его
                    // целостность перед запуском) — доводим до 100%, IsIndeterminate
                    // снимаем на случай, если он включился из-за неизвестного Content-Length.
                    appProgress.Status = "🔐 Проверка SHA256...";
                    appProgress.IsIndeterminate = false;
                    appProgress.Percentage = 100;
                    progress.Report(appProgress);

                    bool tempIsMsi = tempFile.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
                    string silentArgs;
                    if (tempIsMsi)
                    {
                        // MSI запускаем через msiexec в тихом режиме
                        silentArgs = $"/i \"{tempFile}\" /quiet /norestart"
                                     + BuildInstallerLocationArg(true, installDrive, app.DisplayName);
                    }
                    else
                    {
                        silentArgs = app.SilentArgs;
                        if (!CommandLineGuard.ValidateSilentArgs(silentArgs))
                        {
                            AppLogger.Write($"[InstallationService] ⚠ SilentArgs содержит недопустимые символы для {app.DisplayName} — использую /S");
                            silentArgs = "/S";
                        }
                        if (string.IsNullOrWhiteSpace(silentArgs) && ProfileService.Current.SilentInstall)
                            silentArgs = "/S";
                        // Best-effort путь установки для прямого EXE
                        silentArgs += BuildInstallerLocationArg(false, installDrive, app.DisplayName);
                    }

                    // Держим temp-файл открытым с FileShare.Read НЕПРЕРЫВНО от проверки
                    // хеша до завершения установки — верификация читает из уже открытого
                    // хендла, а не из отдельного временного (иначе между закрытием
                    // проверочного хендла и открытием защитного остаётся окно для подмены —
                    // TOCTOU). File.Delete ниже вынесен за using: к моменту удаления хендл
                    // уже освобождён. Зеркалирует защиту лаунчера. SHA256 гарантированно
                    // указан (проверено перед циклом) — верификация выполняется всегда.
                    bool hashOk;
                    (bool Ok, bool Reboot, int ExitCode)? run = null;
                    using (var verifiedStream = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        hashOk = await HashHelper.VerifyHashAsync(verifiedStream, app.Sha256!);
                        if (hashOk)
                        {
                            Log($"✅ SHA256 OK: {app.DisplayName}");
                            token.ThrowIfCancellationRequested();
                            // Переключение фазы: Загрузка завершена, начинается Установка —
                            // сбрасываем Percentage на 0 (не тащим хвост от 100%). Гранулярного
                            // прогресса самого установщика нет (elevated чёрный ящик, формат
                            // произвольный) — честно IsIndeterminate, а не выдуманные проценты.
                            appProgress.Status = "⚙️ Установка...";
                            appProgress.Phase = InstallPhase.Installing;
                            appProgress.IsIndeterminate = true;
                            appProgress.Percentage = 0;
                            progress.Report(appProgress);

                            var psi = new ProcessStartInfo
                            {
                                FileName = tempIsMsi ? TrustedExecutablePaths.MsiExec : tempFile,
                                Arguments = silentArgs,
                                UseShellExecute = true, Verb = "runas",
                                WindowStyle = ProcessWindowStyle.Hidden
                            };

                            run = await RunElevatedInstallerAsync(psi, token,
                                pid => AppLogger.Write($"▶ Запущен процесс установки PID {pid}: {Path.GetFileName(psi.FileName)}"));
                        }
                    }
                    if (!hashOk)
                    {
                        Log($"❌ SHA256 mismatch: {app.DisplayName}");
                        try { File.Delete(tempFile); } catch { }
                        appProgress.Status = "⚠ SHA256 не совпал — пробуем следующий источник";
                        appProgress.Phase = InstallPhase.Error;
                        appProgress.IsIndeterminate = false;
                        progress.Report(appProgress);
                        hashMismatchCount++;
                        continue;
                    }
                    if (run != null)
                    {
                        try { File.Delete(tempFile); } catch { }
                        if (run.Value.Ok)
                            return await ReportInstallSuccessAsync(app, appProgress, progress, run.Value.Reboot, "direct",
                                "✅ Установлено (прямая ссылка)",
                                $"✅ {app.DisplayName} — прямая ссылка: {url}",
                                $"⚠ Установлено. Требуется перезагрузка. {app.DisplayName} — прямая ссылка: {url}",
                                "Установлено",
                                "Установлено (требуется перезагрузка)");
                    }
                }
                // Отмена пользователем — пробрасываем; таймаут заголовков — пробуем следующий источник
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Log($"❌ Прямая ссылка {url}: {ex.Message}");
                    try { File.Delete(tempFile); } catch { }
                }
            }
            if (hashMismatchCount > 0)
                InstallFailureService.Append(app.DisplayName, app.Id, "direct",
                    hashMismatchCount == 1
                        ? "SHA256 mismatch"
                        : $"SHA256 mismatch ({hashMismatchCount} зеркал)");
            return null;
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

        // ── Общий репорт успешной установки: прогресс + лог + история ───────────
        private async Task<(bool Success, string Message, AppInstallProgress Progress)> ReportInstallSuccessAsync(
            AppInfo app, AppInstallProgress appProgress, IProgress<AppInstallProgress> progress,
            bool reboot, string source, string statusOk, string logOk, string logReboot,
            string messageOk, string messageReboot)
        {
            appProgress.Status = reboot ? "⚠ Установлено. Требуется перезагрузка." : statusOk;
            appProgress.Phase = InstallPhase.Done;
            appProgress.IsIndeterminate = false;
            appProgress.Percentage = 100;
            progress.Report(appProgress);
            Log(reboot ? logReboot : logOk);
            if (ProfileService.Current.SaveInstallHistory)
                await InstallHistoryService.Instance.TrackAsync(app.Id, app.DisplayName, source, app.CategoryString);
            return (true, reboot ? messageReboot : messageOk, appProgress);
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

        private async Task<bool> RunWingetAsync(string appId, string source, CancellationToken token, string? version = null, string? installDrive = null)
        {
            var profile = ProfileService.Current;

            // Применяем выбранный диск установки. Для несистемного диска —
            // «{диск}:\Program Files»; иначе используем DefaultInstallFolder из профиля.
            // Обе ветки теперь валидируются одинаково (раньше folderOnSameDrive
            // подставлял DefaultInstallFolder без CommandLineGuard.ValidateInstallFolder).
            string? location = null;
            if (IsNonSystemDrive(installDrive))
            {
                string driveUpper = installDrive!.TrimEnd('\\', '/').ToUpperInvariant();
                bool folderOnSameDrive = !string.IsNullOrWhiteSpace(profile.DefaultInstallFolder) &&
                    profile.DefaultInstallFolder.TrimEnd('\\', '/').ToUpperInvariant().StartsWith(driveUpper) &&
                    CommandLineGuard.ValidateInstallFolder(profile.DefaultInstallFolder);
                location = folderOnSameDrive
                    ? profile.DefaultInstallFolder
                    : ProgramFilesOn(installDrive!);
            }
            else if (!string.IsNullOrWhiteSpace(profile.DefaultInstallFolder) && CommandLineGuard.ValidateInstallFolder(profile.DefaultInstallFolder))
                location = profile.DefaultInstallFolder;

            // msstore/MSIX игнорируют --location или завершаются с ошибкой
            if (source.Equals("msstore", StringComparison.OrdinalIgnoreCase))
                location = null;

            // Версия приходит из внешнего каталога или выбора пользователя —
            // валидируем перед подстановкой в командную строку (паритет с appId).
            if (!string.IsNullOrEmpty(version) && !CommandLineGuard.ValidateId(version))
            {
                Log($"❌ Недопустимая версия «{version}» для {appId} — источник Winget пропущен");
                return false;
            }

            var wingetExe = TrustedExecutablePaths.ResolveWinget();
            if (wingetExe == null)
            {
                Log($"❌ winget не найден по доверенному пути для {appId}");
                return false;
            }
            var psi = new ProcessStartInfo
            {
                FileName = wingetExe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            // Аргументы через ArgumentList — .NET сам экранирует каждый токен, устраняя
            // саму поверхность инъекции (в т.ч. для DefaultInstallFolder из profile.json,
            // который доступен на запись любому не-elevated процессу пользователя).
            psi.ArgumentList.Add("install");
            psi.ArgumentList.Add("--id");
            psi.ArgumentList.Add(appId);
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("--source");
            psi.ArgumentList.Add(source);
            if (!string.IsNullOrEmpty(version))
            {
                psi.ArgumentList.Add("--version");
                psi.ArgumentList.Add(version);
            }
            psi.ArgumentList.Add("--accept-package-agreements");
            psi.ArgumentList.Add("--accept-source-agreements");
            psi.ArgumentList.Add("--disable-interactivity");
            if (profile.SilentInstall) psi.ArgumentList.Add("--silent");
            if (location != null)
            {
                psi.ArgumentList.Add("--location");
                psi.ArgumentList.Add(location);
            }

            using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                process.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data) && !token.IsCancellationRequested)
                    {
                        Log($"Winget [{appId}]: {e.Data}");
                    }
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data) && !token.IsCancellationRequested)
                    {
                        Log($"Winget ERROR [{appId}]: {e.Data}");
                    }
                };

                try
                {
                    process.Start();
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // winget не установлен в системе — не валим весь цикл источников,
                    // просто сообщаем о неудаче, чтобы перейти к следующему источнику.
                    Log($"❌ winget не найден — источник Winget пропущен ({appId})");
                    return false;
                }
                catch (FileNotFoundException)
                {
                    Log($"❌ winget не найден — источник Winget пропущен ({appId})");
                    return false;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                AppLogger.Write($"▶ Запущен процесс установки PID {process.Id}: winget install {appId}");

                while (!process.HasExited)
                {
                    if (token.IsCancellationRequested)
                    {
                        // Kill(true) — убиваем winget и все дочерние процессы (msiexec, setup.exe и др.)
                        try { process.Kill(entireProcessTree: true); } catch { }
                        token.ThrowIfCancellationRequested();
                    }
                    await Task.Delay(100, token);
                }

                // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED — установка прошла успешно
                if (process.ExitCode == 3010)
                    Log($"⚠ Установлено. Требуется перезагрузка. ({appId})");
                return process.ExitCode == 0 || process.ExitCode == 3010;
            }
        }

        private void Log(string message)
        {
            try
            {
                lock (_logLock)
                {
                    File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss} - {message}\n");
                }
            }
            catch { }
        }

        public string GetLogPath() => _logPath;

        public void Dispose()
        {
            // HttpClient общий (static) — живёт всё время работы приложения, не освобождается здесь.
        }
    }

    // INotifyPropertyChanged обязателен: CatalogViewModel переиспользует один и
    // тот же экземпляр на все последующие Progress<AppInstallProgress>-события
    // одного приложения (мутирует Status/Percentage на месте, не пересоздаёт
    // объект) — без уведомления WPF-биндинг ProgressBar.Value="{Binding
    // Percentage}" в CatalogTab.xaml обновляется только на первом событии
    // (когда элемент добавляется в ObservableCollection) и застревает дальше,
    // хотя установка по факту продолжается и завершается. В императивном
    // коде до MVVM-переноса это компенсировалось явным Items.Refresh() —
    // при переносе на биндинг эквивалент потерялся.
    // Явная фаза установки для UI (см. AppInstallProgress.Phase). Порядок значений
    // важен: Download — значение по умолчанию (0), чтобы объект перед первым
    // Report() уже имел осмысленную фазу без явного присвоения.
    public enum InstallPhase
    {
        /// <summary>Загрузка установщика. Используется, только если скачивание
        /// реально происходит (прямая ссылка) — не выставляется искусственно там,
        /// где источник уже локален (кэш, drag-drop) или сам является чёрным
        /// ящиком (winget/choco).</summary>
        Download,
        /// <summary>Установка — после того как файл получен (или сразу, если
        /// скачивания как такового не было).</summary>
        Installing,
        /// <summary>Завершено успешно (в т.ч. с отложенной перезагрузкой).</summary>
        Done,
        /// <summary>Завершено с ошибкой либо отменено пользователем.</summary>
        Error
    }

    public class AppInstallProgress : INotifyPropertyChanged
    {
        public string AppId { get; set; } = string.Empty;
        public string AppName { get; set; } = string.Empty;

        private string _status = string.Empty;
        public string Status
        {
            get => _status;
            set => SetField(ref _status, value);
        }

        private int _percentage;
        public int Percentage
        {
            get => _percentage;
            set => SetField(ref _percentage, value);
        }

        private bool _isIndeterminate;
        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            set => SetField(ref _isIndeterminate, value);
        }

        private InstallPhase _phase = InstallPhase.Download;
        /// <summary>
        /// Текущая фаза установки. Percentage считается заново в каждой фазе
        /// (полные 0-100% на скачивание, отдельно 0-100% на установку) — раньше
        /// обе фазы были замешаны в одну шкалу 0-100, из-за чего полоска прогресса
        /// была нечитаемой (пользовательский фидбек 2026-07-24). UI (CatalogTab)
        /// красит полоску по этому свойству через InstallPhaseToBrushConverter.
        /// </summary>
        public InstallPhase Phase
        {
            get => _phase;
            set => SetField(ref _phase, value);
        }

        /// <summary>
        /// Сквозная оценка прогресса (0-100) для агрегированной шкалы по всей
        /// очереди установки (CatalogViewModel.OverallProgressPercentage). Без
        /// неё агрегат «прыгал» бы назад в момент переключения Download →
        /// Installing, когда Percentage сбрасывается на 0 для новой фазы.
        /// Взвешено 50/50 между фазами; для IsIndeterminate (нет гранулярных
        /// данных о ходе фазы) берётся середина её диапазона — честная оценка
        /// «примерно на этом этапе», а не выдуманный точный процент.
        /// </summary>
        public double EffectiveProgress => Phase switch
        {
            InstallPhase.Download => IsIndeterminate ? 25.0 : Percentage * 0.5,
            InstallPhase.Installing => IsIndeterminate ? 75.0 : 50.0 + Percentage * 0.5,
            InstallPhase.Done => 100.0,
            InstallPhase.Error => 100.0,
            _ => Percentage
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
