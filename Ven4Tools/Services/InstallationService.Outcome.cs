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
        // ── Проверка результата установки по факту ──────────────────────────────

        /// <summary>Снимок состояния «установлено ли приложение» на момент проверки
        /// (используется и как baseline до установки, и как итог после неё).</summary>
        private readonly struct InstalledBaseline
        {
            /// <summary>Есть ли вообще надёжный ID для сверки с winget list.</summary>
            public bool VerificationSupported { get; init; }
            public bool WasInstalled { get; init; }
            public string? Version { get; init; }

            public static readonly InstalledBaseline Unsupported = new() { VerificationSupported = false };
        }

        // Ретраи на «нашли / не нашли» после установки — не мгновенная единичная
        // проверка. Индекс winget (реестр/ARP) может не успеть отразить только что
        // завершившуюся установку сразу же — без паузы это источник ложных
        // Unconfirmed. Задержки нарастающие, суммарно ~1.4 с — не бесконечно, но
        // достаточно для типичной задержки записи в реестр установщиком.
        private static readonly TimeSpan[] VerificationRetryDelays =
            { TimeSpan.Zero, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(900) };

        /// <summary>
        /// ID для сверки с winget list — тот же способ (AlternativeId ?? Id), что уже
        /// использует бейдж «установлено» в каталоге (CatalogViewModel). «User.»-префикс —
        /// синтетический ID для приложений без каталожной записи в winget (добавлены
        /// вручную только с ChocoId, см. CatalogViewModel.AddChocoSuggestion) — winget list
        /// никогда не найдёт такой токен, сверка была бы фиктивной, поэтому такие ID
        /// заведомо помечаются как «проверка недоступна» (тот же признак, что уже
        /// использует InstallFromWingetAsync, чтобы вообще не пытаться ставить через winget).
        /// </summary>
        private static bool IsVerifiableId(string outcomeCheckId) =>
            !string.IsNullOrEmpty(outcomeCheckId) && !outcomeCheckId.StartsWith("User.", StringComparison.Ordinal);

        private static async Task<InstalledBaseline> CaptureInstalledBaselineAsync(string outcomeCheckId)
        {
            if (!IsVerifiableId(outcomeCheckId)) return InstalledBaseline.Unsupported;

            var checker = new InstalledAppsService();
            await checker.RefreshAsync();
            bool found = checker.IsInstalled(outcomeCheckId);
            return new InstalledBaseline
            {
                VerificationSupported = true,
                WasInstalled = found,
                Version = found ? checker.GetInstalledVersion(outcomeCheckId) : null
            };
        }

        private static async Task<InstalledBaseline> VerifyInstalledWithRetryAsync(string outcomeCheckId, CancellationToken token)
        {
            if (!IsVerifiableId(outcomeCheckId)) return InstalledBaseline.Unsupported;

            foreach (var delay in VerificationRetryDelays)
            {
                if (delay > TimeSpan.Zero)
                {
                    try { await Task.Delay(delay, token); }
                    catch (OperationCanceledException)
                    {
                        // Отмена во время до-проверки уже завершившегося установщика не
                        // должна обнулять сам факт того, что процесс уже отработал —
                        // просто прекращаем ретраи с тем, что успели узнать.
                        break;
                    }
                }

                var checker = new InstalledAppsService();
                await checker.RefreshAsync();
                if (checker.IsInstalled(outcomeCheckId))
                    return new InstalledBaseline
                    {
                        VerificationSupported = true,
                        WasInstalled = true,
                        Version = checker.GetInstalledVersion(outcomeCheckId)
                    };
            }
            return new InstalledBaseline { VerificationSupported = true, WasInstalled = false };
        }

        // ── Общий репорт результата установки: сверка по факту + прогресс + лог ──
        // Единая точка для ВСЕХ путей установки (winget, choco, прямая ссылка,
        // локальный установщик, офлайн-кэш) и для двух терминальных точек неудачи
        // (локальный установщик без фолбэка, цепочка источников исчерпана) — код
        // выхода процесса здесь не финальный вердикт, а только одна из вводных для
        // InstallOutcomeEvaluator наравне со сверкой по факту.
        private async Task<(bool Success, string Message, AppInstallProgress Progress)> ReportInstallOutcomeAsync(
            AppInfo app, AppInstallProgress appProgress, IProgress<AppInstallProgress> progress,
            string outcomeCheckId, InstalledBaseline baseline,
            bool exitCodeSuccess, bool reboot, string source, string sourceLabel,
            CancellationToken token, string? logDetail = null)
        {
            if (baseline.VerificationSupported)
            {
                appProgress.Status = "🔍 Проверка результата...";
                appProgress.IsIndeterminate = true;
                progress.Report(appProgress);
            }

            var post = await VerifyInstalledWithRetryAsync(outcomeCheckId, token);

            var outcome = InstallOutcomeEvaluator.Evaluate(new InstallOutcomeContext
            {
                VerificationSupported = baseline.VerificationSupported,
                ExitCodeSuccess = exitCodeSuccess,
                WasInstalledBefore = baseline.WasInstalled,
                VersionBefore = baseline.Version,
                FoundAfter = post.WasInstalled,
                VersionAfter = post.Version
            });

            bool success = outcome switch
            {
                InstallOutcome.ConfirmedFailure => false,
                // Проверка недоступна/не выполнялась — как и раньше, доверяем коду
                // выхода целиком (не регрессия для путей без надёжного ID).
                InstallOutcome.NotYetDetermined => exitCodeSuccess,
                _ => true
            };

            string detailSuffix = string.IsNullOrEmpty(logDetail) ? "" : $" — {logDetail}";
            string status, logLine, message;

            switch (outcome)
            {
                case InstallOutcome.ConfirmedSuccess:
                    status = reboot ? "⚠ Установлено. Требуется перезагрузка." : $"✅ Установлено ({sourceLabel})";
                    logLine = reboot
                        ? $"⚠ {app.DisplayName} — {sourceLabel}: установлено (подтверждено по факту), требуется перезагрузка{detailSuffix}"
                        : $"✅ {app.DisplayName} — {sourceLabel}: установлено (подтверждено по факту){detailSuffix}";
                    message = reboot ? "Установлено (требуется перезагрузка)" : "Установлено";
                    break;

                case InstallOutcome.AlreadyUpToDate:
                    // Не «только что поставили» — версия до и после установки совпадает,
                    // хотя код выхода мог быть успешным (типичный no-op тихого инсталлятора).
                    status = "ℹ️ Уже установлено — версия не изменилась";
                    logLine = $"ℹ️ {app.DisplayName} — {sourceLabel}: уже было установлено, версия «{baseline.Version}» не изменилась{detailSuffix}";
                    message = "Уже установлено (версия не изменилась)";
                    break;

                case InstallOutcome.Unconfirmed:
                    // Код выхода успешный, но по факту в системе не нашли даже после
                    // ретраев — честно не пишем «Установлено» без оговорки.
                    status = reboot
                        ? "⚠ Установлено, требуется перезагрузка (не удалось подтвердить финальное состояние до перезагрузки)"
                        : "⚠ Похоже, установлено — не удалось подтвердить";
                    logLine = reboot
                        ? $"⚠ {app.DisplayName} — {sourceLabel}: код выхода успешный, требуется перезагрузка, но по факту в системе не найдено (часть инсталляторов дописывают реестр только после перезагрузки){detailSuffix}"
                        : $"⚠ {app.DisplayName} — {sourceLabel}: код выхода успешный, но по факту в системе не найдено даже после повторных проверок{detailSuffix}";
                    message = reboot ? "Установлено, требуется перезагрузка (не подтверждено)" : "Не удалось подтвердить установку";
                    break;

                case InstallOutcome.ConfirmedFailure:
                    status = "❌ Не установлено";
                    logLine = $"❌ {app.DisplayName} — {sourceLabel}: не установлено (код выхода — ошибка, по факту в системе не найдено){detailSuffix}";
                    message = "Не установлено";
                    InstallFailureService.Append(app.DisplayName, app.Id, source,
                        string.IsNullOrEmpty(logDetail) ? "Код выхода — ошибка" : logDetail);
                    break;

                default: // NotYetDetermined — надёжного ID для сверки нет, доверяем коду выхода как раньше
                    if (exitCodeSuccess)
                    {
                        status = reboot ? "⚠ Установлено. Требуется перезагрузка." : $"✅ Установлено ({sourceLabel})";
                        logLine = reboot
                            ? $"⚠ {app.DisplayName} — {sourceLabel}: установлено, требуется перезагрузка (проверка по факту недоступна){detailSuffix}"
                            : $"✅ {app.DisplayName} — {sourceLabel} (проверка по факту недоступна){detailSuffix}";
                        message = reboot ? "Установлено (требуется перезагрузка)" : "Установлено";
                    }
                    else
                    {
                        status = "❌ Ошибка установки";
                        logLine = $"❌ {app.DisplayName} — {sourceLabel}: код выхода — ошибка (проверка по факту недоступна){detailSuffix}";
                        message = "Ошибка установки";
                        InstallFailureService.Append(app.DisplayName, app.Id, source,
                            string.IsNullOrEmpty(logDetail) ? "Код выхода — ошибка" : logDetail);
                    }
                    break;
            }

            appProgress.Status = status;
            appProgress.Outcome = outcome;
            appProgress.IsIndeterminate = false;
            if (success) appProgress.Percentage = 100;
            appProgress.Phase = success ? InstallPhase.Done : InstallPhase.Error;
            progress.Report(appProgress);
            Log(logLine);

            // История/трекер версий — только для тех исходов, где реально что-то
            // изменилось (ConfirmedSuccess) или где иного нет и мы доверяем коду
            // выхода как раньше (NotYetDetermined). AlreadyUpToDate и Unconfirmed
            // намеренно исключены: ни «переустановка» не подтверждена, ни то, что
            // приложение вообще появилось — фиксировать в истории нечего.
            bool trackHistory = outcome == InstallOutcome.ConfirmedSuccess
                || (outcome == InstallOutcome.NotYetDetermined && exitCodeSuccess);
            if (trackHistory && ProfileService.Current.SaveInstallHistory)
                await InstallHistoryService.Instance.TrackAsync(app.Id, app.DisplayName, source, app.CategoryString);

            return (success, message, appProgress);
        }
    }
}
