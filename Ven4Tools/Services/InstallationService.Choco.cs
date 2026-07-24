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
        // ── Источник: Chocolatey ───────────────────────────────────────────────
        private async Task<(bool Success, string Message, AppInstallProgress Progress)?> InstallFromChocoAsync(
            AppInfo app, AppInstallProgress appProgress, IProgress<AppInstallProgress> progress,
            Func<string, Task<bool>>? confirmPmInstall, string outcomeCheckId, InstalledBaseline baseline,
            CancellationToken token)
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
                    && await PackageManagerService.InstallChocoAsync(token, msg => Log(msg)));
            if (chocoOk && await PackageManagerService.RunChocoInstallAsync(app.ChocoId, token, msg => Log(msg)))
                // Choco (RunChocoInstallAsync) не различает 0 и 3010 на возврате —
                // reboot здесь всегда false, честно (не выдумываем то, чего сейчас
                // не видно), в отличие от winget/elevated-путей, где это различие есть.
                return await ReportInstallOutcomeAsync(app, appProgress, progress, outcomeCheckId, baseline,
                    true, false, "choco", "Chocolatey", token);
            return null;
        }
    }
}
