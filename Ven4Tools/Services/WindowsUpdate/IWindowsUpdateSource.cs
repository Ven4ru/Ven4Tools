using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services.WindowsUpdate
{
    /// <summary>
    /// Абстракция над Windows Update Agent. Единственная реализация в проде —
    /// WindowsUpdateComSource (COM). В тестах — FakeWindowsUpdateSource, без реального API.
    /// </summary>
    public interface IWindowsUpdateSource
    {
        /// <summary>Служба Windows Update (wuauserv) запущена?</summary>
        bool IsServiceRunning();

        /// <summary>Попытаться запустить службу. true — удалось (или уже была запущена).</summary>
        bool TryStartService();

        /// <summary>Требуется перезагрузка от предыдущей установки?</summary>
        bool IsRebootPending();

        Task<WindowsUpdateSearchResult> SearchAsync(CancellationToken ct);

        /// <summary>
        /// Скачивает и устанавливает патчи по UpdateId. Реализация обязана заново
        /// найти патчи по актуальному поиску внутри себя, а не доверять только списку ID —
        /// список могут выбрать в одном состоянии системы, а установка стартовать позже.
        /// </summary>
        Task<WindowsUpdateInstallOutcome> InstallAsync(
            IReadOnlyList<string> updateIds,
            IProgress<WindowsUpdateProgress> progress,
            CancellationToken ct);

        /// <summary>
        /// Скачивает патчи по UpdateId, НЕ устанавливая их — файлы просто ложатся в кэш
        /// Windows Update, чтобы последующая установка по клику пользователя стартовала
        /// сразу, без ожидания загрузки. Как и InstallAsync, реализация обязана заново
        /// найти патчи актуальным поиском, а не доверять списку ID вслепую.
        /// Реализация НИКОГДА не должна вызывать установщик (CreateUpdateInstaller/Install) —
        /// железное правило приложения: патчи ставятся только по явному клику пользователя.
        /// </summary>
        Task<WindowsUpdateDownloadOutcome> DownloadOnlyAsync(
            IReadOnlyList<string> updateIds,
            IProgress<WindowsUpdateProgress> progress,
            CancellationToken ct);
    }
}
