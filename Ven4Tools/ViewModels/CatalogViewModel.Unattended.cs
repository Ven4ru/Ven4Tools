using System;
using System.Linq;
using System.Threading.Tasks;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    // Установка набора по заданию — из командной строки или файла ответа
    // (см. Services/UnattendedRequest.cs). Часть CatalogViewModel. Идёт тем же путём,
    // что и кнопка «Установить»: отмечает строки каталога и запускает обычную пачку,
    // поэтому проверки источников, хеши и журнал сбоев работают без отдельной копии.
    public sealed partial class CatalogViewModel
    {
        private Task? _initialLoad;

        /// <summary>
        /// Первая загрузка каталога — одна на сеанс, кто бы её ни запросил первым:
        /// вкладка при открытии или тихий режим, которому нужно дождаться её конца.
        /// </summary>
        public Task EnsureLoadedAsync() => _initialLoad ??= LoadAsync();

        public async Task<UnattendedReport> RunUnattendedAsync(UnattendedRequest request)
        {
            var report = new UnattendedReport { StartedUtc = DateTime.UtcNow.ToString("o") };
            UnattendedReport Finish(int exitCode, string message)
            {
                report.ExitCode = exitCode;
                report.Message = message;
                report.FinishedUtc = DateTime.UtcNow.ToString("o");
                Log($"🤖 Установка по заданию: {message}");
                return report;
            }

            Log($"🤖 Установка по заданию: приложений в задании — {request.AppIds.Count}" +
                (request.Silent ? ", тихий режим" : ""));

            await EnsureLoadedAsync();
            if (Apps.Count == 0)
                return Finish(UnattendedExitCode.NothingToInstall, "каталог не загрузился — устанавливать нечего");

            if (request.InstallDrive != null)
            {
                var disk = AvailableDisks.FirstOrDefault(
                    d => string.Equals(d.Name, request.InstallDrive, StringComparison.OrdinalIgnoreCase));
                if (disk == null)
                    return Finish(UnattendedExitCode.InvalidRequest, $"диск {request.InstallDrive} недоступен");
                SelectedDisk = disk;
            }

            // Отметки, оставшиеся от прошлых действий, в задание не входят.
            foreach (var row in Apps.Where(a => a.IsSelected).ToList()) row.IsSelected = false;

            foreach (string id in request.AppIds)
            {
                var row = Apps.FirstOrDefault(a => string.Equals(a.AppId, id, StringComparison.OrdinalIgnoreCase));
                if (row == null) report.NotFound.Add(id);
                else if (!row.IsSelectable) report.Unavailable.Add(row.AppId);
                else row.IsSelected = true;
            }

            if (!Apps.Any(a => a.IsSelected))
                return Finish(UnattendedExitCode.NothingToInstall,
                    "ни одно приложение из задания не найдено в каталоге или недоступно для установки");

            var result = await InstallSelectedAsync(request);
            if (result == null)
                return Finish(UnattendedExitCode.NothingToInstall, "установка не началась");

            report.Installed.AddRange(result.Installed.Select(r => r.AppId));
            report.Failed.AddRange(result.Failed.Select(f => new UnattendedFailure { Id = f.Row.AppId, Reason = f.Message }));
            report.RebootRequired.AddRange(result.RebootRequired.Select(r => r.AppId));

            // Отменённая пачка может оставить часть приложений не начатыми: в отчёте
            // они должны быть видны, а не пропасть между «установлено» и «ошибка».
            if (result.Cancelled)
            {
                foreach (var row in Apps.Where(a => a.IsSelected && !a.JustInstalled))
                    if (report.Failed.All(f => f.Id != row.AppId))
                        report.Failed.Add(new UnattendedFailure { Id = row.AppId, Reason = "установка отменена" });
            }

            bool allGood = report.Failed.Count == 0 && report.NotFound.Count == 0 && report.Unavailable.Count == 0;
            return Finish(
                allGood ? UnattendedExitCode.Success : UnattendedExitCode.PartialFailure,
                $"установлено {report.Installed.Count}, не удалось {report.Failed.Count}, " +
                $"нет в каталоге {report.NotFound.Count}, недоступно {report.Unavailable.Count}");
        }
    }
}
