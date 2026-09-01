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

        /// <summary>
        /// Тихо скачивает патчи, ничего не устанавливая — фоновый режим
        /// «Уведомлять и скачивать в фоне». Установку не запускает ни при каких условиях.
        /// </summary>
        public async Task<WindowsUpdateDownloadOutcome> DownloadOnlyAsync(
            IReadOnlyList<string> updateIds,
            IProgress<WindowsUpdateProgress> progress,
            CancellationToken ct)
        {
            if (updateIds.Count == 0)
                return new WindowsUpdateDownloadOutcome { Success = false, ErrorMessage = "Нечего скачивать." };

            // Проверка IsRebootPending здесь намеренно НЕ делается (в отличие от
            // InstallSelectedAsync): незавершённая перезагрузка мешает ставить патчи,
            // но не мешает складывать их в кэш. Наоборот — именно в этот момент полезно
            // скачать заранее, чтобы после перезагрузки установка стартовала мгновенно.

            // Семафор берётся неблокирующей попыткой, а не WaitAsync: фоновое скачивание
            // может идти десятки минут, и обычное ожидание отдало бы ему очередь раньше
            // пользовательской установки из каталога, которая ждёт тот же семафор.
            // Не получилось занять — молча пропускаем цикл, следующая фоновая проверка
            // через 6 часов повторит.
            if (!await InstallationService.InstallSemaphore.WaitAsync(0, ct))
                return new WindowsUpdateDownloadOutcome
                {
                    Success = false,
                    ErrorMessage = "Идёт установка приложений — фоновое скачивание отложено до следующей проверки."
                };

            try
            {
                return await _source.DownloadOnlyAsync(updateIds, progress, ct);
            }
            finally
            {
                InstallationService.InstallSemaphore.Release();
            }
        }
    }
}
