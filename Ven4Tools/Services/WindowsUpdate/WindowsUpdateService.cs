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

        // Собственный семафор операций с Windows Update — намеренно отдельный от
        // InstallationService.InstallSemaphore. Общий семафор приложения существует
        // ради одной задачи: не давать запуститься параллельным msiexec (ошибка
        // Windows Installer 1618). Фоновое скачивание патчей складывает файлы в кэш
        // Windows Update и msiexec не трогает вообще, поэтому держать на нём общий
        // семафор десятки минут означало бы без всякой причины блокировать установку
        // из каталога/истории/пинов — да ещё с ложным сообщением «дождитесь завершения
        // текущей установки». Между собой WU-операции (скачивание и установка патчей)
        // по-прежнему строго последовательны — это и обеспечивает этот семафор.
        private static readonly SemaphoreSlim WuSemaphore = new SemaphoreSlim(1, 1);

        // Признак идущего фонового скачивания — отдельным флагом, а не по семафору:
        // семафор занят и во время установки патчей, а UI нужно отличать одно от
        // другого (пилюля активных задач в шапке главного окна).
        private static int _backgroundDownloadActive;

        private const string BackgroundDownloadBusyMessage =
            "Сейчас идёт фоновое скачивание обновлений Windows — дождитесь его завершения, затем повторите попытку.";

        /// <summary>
        /// true, пока идёт тихое фоновое скачивание патчей
        /// (<see cref="DownloadOnlyAsync"/>). Установку приложений не блокирует —
        /// нужен только для честного текста статуса в интерфейсе.
        /// </summary>
        public static bool IsDownloadingInBackground => Volatile.Read(ref _backgroundDownloadActive) > 0;

        /// <summary>true, если сейчас идёт любая операция с обновлениями Windows.</summary>
        public static bool IsWindowsUpdateBusy => WuSemaphore.CurrentCount == 0;

        // Единый источник истины на «можно ли сейчас ставить патчи»: мешает и
        // системная установка приложений (общая MSI-подсистема), и своя же
        // WU-операция, уже идущая в фоне.
        public static bool IsBusy => InstallationService.IsBusy || IsWindowsUpdateBusy;

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

            // Две разные причины отказа — два разных сообщения. Раньше обе прятались
            // за одним «дождитесь установки приложений из каталога», хотя фоновое
            // скачивание патчей никакой установкой не является.
            if (InstallationService.IsBusy)
                return new WindowsUpdateInstallOutcome
                {
                    Success = false,
                    ErrorMessage = "Дождитесь завершения установки приложений из каталога, затем повторите попытку."
                };

            if (IsWindowsUpdateBusy)
                return new WindowsUpdateInstallOutcome
                {
                    Success = false,
                    ErrorMessage = BackgroundDownloadBusyMessage
                };

            if (_source.IsRebootPending())
                return new WindowsUpdateInstallOutcome
                {
                    Success = false,
                    ErrorMessage = "Требуется перезагрузка от предыдущей установки обновлений — установить новые патчи можно после неё."
                };

            // Установка патчей, в отличие от скачивания, общую MSI-подсистему
            // затрагивает — поэтому берётся и общий семафор приложения, и свой
            // WU-семафор.
            //
            // WU-семафор берётся ТОЛЬКО неблокирующей попыткой, и это принципиально.
            // Проверка IsWindowsUpdateBusy выше — подсказка для сообщения, а не защёлка:
            // фоновое скачивание живёт на пуле потоков и может занять WU-семафор в зазоре
            // между этой проверкой и захватом. Блокирующее ожидание в этот момент означало
            // бы стоять десятки минут (пока качается накопительный пакет) УДЕРЖИВАЯ общий
            // семафор приложения — то есть ровно та ложная блокировка каталога/пинов/истории
            // сообщением «дождитесь завершения текущей установки», ради устранения которой
            // семафоры и разводились. Поэтому: не получилось занять WU-семафор сразу —
            // сразу же отпускаем общий семафор (через finally ниже) и честно отказываем.
            await InstallationService.InstallSemaphore.WaitAsync(ct);
            try
            {
                if (!await WuSemaphore.WaitAsync(0, ct))
                    return new WindowsUpdateInstallOutcome
                    {
                        Success = false,
                        ErrorMessage = BackgroundDownloadBusyMessage
                    };

                try
                {
                    return await _source.InstallAsync(updateIds, progress, ct);
                }
                finally
                {
                    WuSemaphore.Release();
                }
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
                return new WindowsUpdateDownloadOutcome { ErrorMessage = "Нечего скачивать." };

            // Проверка IsRebootPending здесь намеренно НЕ делается (в отличие от
            // InstallSelectedAsync): незавершённая перезагрузка мешает ставить патчи,
            // но не мешает складывать их в кэш. Наоборот — именно в этот момент полезно
            // скачать заранее, чтобы после перезагрузки установка стартовала мгновенно.

            // Берётся ТОЛЬКО свой WU-семафор, и неблокирующей попыткой. Общий семафор
            // приложения здесь не нужен (msiexec скачивание не запускает) и вреден:
            // загрузка большого накопительного пакета идёт десятки минут, и всё это
            // время пользователь не смог бы поставить ничего из каталога, видя при
            // этом неверное «дождитесь завершения текущей установки».
            // Неблокирующая попытка — чтобы фоновая задача не вставала в очередь за
            // установкой патчей: не получилось занять, молча пропускаем цикл,
            // следующая фоновая проверка через 6 часов повторит.
            if (!await WuSemaphore.WaitAsync(0, ct))
                return new WindowsUpdateDownloadOutcome
                {
                    ErrorMessage = "Идёт другая операция с обновлениями Windows — фоновое скачивание отложено до следующей проверки."
                };

            Interlocked.Increment(ref _backgroundDownloadActive);
            try
            {
                return await _source.DownloadOnlyAsync(updateIds, progress, ct);
            }
            finally
            {
                Interlocked.Decrement(ref _backgroundDownloadActive);
                WuSemaphore.Release();
            }
        }
    }
}
