using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services.WindowsUpdate
{
    public sealed class WindowsUpdateService
    {
        private readonly IWindowsUpdateSource _source;

        public WindowsUpdateService(IWindowsUpdateSource? source = null)
        {
            _source = source ?? new WindowsUpdateComSource();
        }

        // Единый источник истины на "идёт ли сейчас системная установка" — общий
        // с каталогом/историей (см. Task 5), а не отдельный флаг.
        public static bool IsBusy => InstallationService.IsBusy;

        public bool IsServiceRunning() => _source.IsServiceRunning();
        public bool TryStartService() => _source.TryStartService();
        public bool IsRebootPending() => _source.IsRebootPending();

        public Task<WindowsUpdateSearchResult> SearchAsync(CancellationToken ct) =>
            _source.SearchAsync(ct);

        public async Task<WindowsUpdateInstallOutcome> InstallSelectedAsync(
            IReadOnlyList<string> updateIds,
            IProgress<WindowsUpdateProgress> progress,
            CancellationToken ct)
        {
            if (updateIds.Count == 0)
                return new WindowsUpdateInstallOutcome { Success = false, ErrorMessage = "Ничего не выбрано." };

            if (IsBusy)
                return new WindowsUpdateInstallOutcome
                {
                    Success = false,
                    ErrorMessage = "Дождитесь завершения установки приложений из каталога, затем повторите попытку."
                };

            if (_source.IsRebootPending())
                return new WindowsUpdateInstallOutcome
                {
                    Success = false,
                    ErrorMessage = "Требуется перезагрузка от предыдущей установки обновлений — установить новые патчи можно после неё."
                };

            await InstallationService.InstallSemaphore.WaitAsync(ct);
            try
            {
                return await _source.InstallAsync(updateIds, progress, ct);
            }
            finally
            {
                InstallationService.InstallSemaphore.Release();
            }
        }
    }
}
