using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Helpers;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Services
{
    public partial class InstallationService
    {
        // ── Стратегия 1: локальный установщик (drag-drop) ──────────────────────
        private async Task<(bool Success, string Message, AppInstallProgress Progress)> InstallFromLocalAsync(
            AppInfo app, AppInstallProgress appProgress, IProgress<AppInstallProgress> progress,
            string installDrive, string outcomeCheckId, InstalledBaseline baseline, CancellationToken token)
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

            // Локальный установщик — единственный путь без фолбэка на другой источник,
            // поэтому это терминальная точка и для успеха, и для неудачи: в обоих
            // случаях сверяем с фактическим состоянием системы, а не только с кодом
            // выхода (плохой код мог быть у установщика, который на самом деле справился).
            return await ReportInstallOutcomeAsync(app, appProgress, progress, outcomeCheckId, baseline,
                run.Value.Ok, run.Value.Reboot, "local", "локальный установщик", token,
                run.Value.Ok ? null : $"код выхода {run.Value.ExitCode}");
        }

        // ── Стратегия 2: офлайн-кэш ────────────────────────────────────────────
        // Возвращает null, если кэша нет / хеш не сошёлся / установка из кэша не
        // удалась — в этих случаях диспетчер продолжает следующими стратегиями.
        private async Task<(bool Success, string Message, AppInstallProgress Progress)?> InstallFromCacheAsync(
            AppInfo app, AppInstallProgress appProgress, IProgress<AppInstallProgress> progress,
            string installDrive, string outcomeCheckId, InstalledBaseline baseline, CancellationToken token)
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
                return await ReportInstallOutcomeAsync(app, appProgress, progress, outcomeCheckId, baseline,
                    true, run.Value.Reboot, "cache", "офлайн-кэш", token);

            // Неудача из кэша не терминальна — диспетчер пробует следующую стратегию
            // (цепочку источников), которая сама сверит результат по факту, если тоже
            // окажется терминальной неудачей. Проверять здесь нечего.
            return null;
        }
    }
}
