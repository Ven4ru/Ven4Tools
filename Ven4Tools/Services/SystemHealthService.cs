using System;

namespace Ven4Tools.Services
{
    internal enum RebootCategory
    {
        Bsod,
        FastStartupFailure,
        PossiblePowerLoss
    }

    internal sealed class RebootEvent
    {
        public DateTime TimeCreated { get; init; }
        public int BugcheckCode { get; init; }
        public bool HasFastStartupFailureNearby { get; init; }
        public bool HasCleanShutdownNearby { get; init; }
    }

    /// <summary>
    /// Классифицирует событие Kernel-Power ID 41 («нештатное завершение
    /// работы»). Порядок проверок важен: настоящий BSOD (BugcheckCode ≠ 0)
    /// всегда побеждает сбой резюме Fast Startup, даже если оба признака
    /// присутствуют одновременно.
    /// </summary>
    internal static class RebootClassifier
    {
        public static RebootCategory? Classify(RebootEvent evt)
        {
            if (evt.HasCleanShutdownNearby) return null;
            if (evt.BugcheckCode != 0) return RebootCategory.Bsod;
            if (evt.HasFastStartupFailureNearby) return RebootCategory.FastStartupFailure;
            return RebootCategory.PossiblePowerLoss;
        }
    }
}
