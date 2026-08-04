using System;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public partial class InstallationService
    {
        // ── Источник: Chocolatey ───────────────────────────────────────────────
        private async Task<SourceAttempt> InstallFromChocoAsync(
            AppInfo app, AppInstallProgress appProgress, IProgress<AppInstallProgress> progress,
            Func<string, Task<bool>>? confirmPmInstall, string outcomeCheckId, InstalledBaseline baseline,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(app.ChocoId)) return SourceAttempt.Failed(null);
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
                    && await PackageManagerService.InstallChocoAsync(token, msg => Log(msg)));
            if (chocoOk)
            {
                var chocoRun = await PackageManagerService.RunChocoInstallAsync(app.ChocoId, token, msg => Log(msg));
                if (chocoRun.Ok)
                    // Choco (RunChocoInstallAsync) не различает 0 и 3010 на возврате —
                    // reboot здесь всегда false, честно (не выдумываем то, чего сейчас
                    // не видно), в отличие от winget/elevated-путей, где это различие есть.
                    return SourceAttempt.Finished(await ReportInstallOutcomeAsync(
                        app, appProgress, progress, outcomeCheckId, baseline,
                        true, false, "choco", token));

                // -1 — синтетический признак «choco вообще не запускался» (невалидный
                // ID, исполняемый файл не найден, общее исключение) — RunChocoInstallAsync
                // уже залогировал точную причину выше по стеку. ChocoErrorMapper
                // хранит для -1 конкретно «не ответил вовремя», что было бы неправдой
                // для остальных трёх случаев — расшифровывать нечего, как и в
                // InstallFromWingetAsync (см. комментарий там же).
                if (chocoRun.ExitCode == -1) return SourceAttempt.Failed(null);

                // Код выхода choco раньше затирался до bool — теперь расшифровываем
                // его в читаемую причину для лога и блока «Не установлено».
                string failureDetail = ChocoErrorMapper.MapExitCode(chocoRun.ExitCode);
                Log($"❌ Choco ({app.ChocoId}): {failureDetail}");
                return SourceAttempt.Failed(failureDetail);
            }
            return SourceAttempt.Failed(null);
        }
    }
}
