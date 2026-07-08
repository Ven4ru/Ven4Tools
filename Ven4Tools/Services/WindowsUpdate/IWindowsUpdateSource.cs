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
    }
}
