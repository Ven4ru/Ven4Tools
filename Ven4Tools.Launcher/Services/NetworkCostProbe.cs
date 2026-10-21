using System;
using System.Runtime.InteropServices;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Лимитное ли сейчас подключение к интернету. Источник — Network List Manager
    /// (COM-интерфейс INetworkCostManager, есть в Windows 8+ и, значит, в 10 и 11):
    /// это тот же ответ, что отдаёт WinRT
    /// NetworkInformation.GetInternetConnectionProfile().GetConnectionCost(), но без
    /// перехода целевой платформы лаунчера на net10.0-windows10.0.x (он тянул бы
    /// проекции WinRT в однофайловую сборку) и без новых пакетов.
    ///
    /// Правило отказа: любая ошибка API — «безлимитное». Ошибиться в эту сторону
    /// значит скачать обновление как раньше; в обратную — молча перестать обновляться.
    /// </summary>
    internal static class NetworkCostProbe
    {
        // NLM_CONNECTION_COST (netlistmgr.h).
        internal const uint CostUnknown = 0x0;
        internal const uint CostUnrestricted = 0x1;
        internal const uint CostFixed = 0x2;
        internal const uint CostVariable = 0x4;
        internal const uint CostOverDataLimit = 0x10000;
        internal const uint CostCongested = 0x20000;
        internal const uint CostRoaming = 0x40000;
        internal const uint CostApproachingDataLimit = 0x80000;

        /// <summary>
        /// Лимитное ли подключение по маске стоимости: тариф с лимитом (Fixed),
        /// оплата за трафик (Variable), роуминг или уже превышенный лимит. Unknown
        /// (0) — безлимитное: так Windows отвечает, когда сама не знает. «Перегружено»
        /// и «лимит близко» сами по себе лимитным подключение не делают — при лимитном
        /// тарифе рядом всегда стоит Fixed. Чистая функция — покрыта unit-тестами.
        /// </summary>
        internal static bool IsMeteredCost(uint cost) =>
            (cost & (CostFixed | CostVariable | CostRoaming | CostOverDataLimit)) != 0;

        /// <summary>
        /// Текущая стоимость подключения или null, если узнать не удалось.
        /// </summary>
        internal static uint? TryGetCost()
        {
            object? manager = null;
            try
            {
                Type? type = Type.GetTypeFromCLSID(NetworkListManagerClsid, throwOnError: false);
                if (type == null) return null;
                manager = Activator.CreateInstance(type);
                if (manager is not INetworkCostManager costManager) return null;

                // pDestIPAddr = NULL — стоимость подключения к интернету в целом,
                // а не до конкретного адреса.
                int hr = costManager.GetCost(out uint cost, IntPtr.Zero);
                return hr == 0 ? cost : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (manager != null && Marshal.IsComObject(manager))
                {
                    try { Marshal.ReleaseComObject(manager); } catch { }
                }
            }
        }

        /// <summary>
        /// Лимитное ли подключение сейчас. Нет данных — false (см. правило отказа).
        /// </summary>
        internal static bool IsCurrentConnectionMetered()
        {
            uint? cost = TryGetCost();
            return cost.HasValue && IsMeteredCost(cost.Value);
        }

        private static readonly Guid NetworkListManagerClsid = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

        // Порядок методов повторяет vtable из netlistmgr.h: GetCost, GetDataPlanStatus,
        // SetDestinationAddresses. Используется только первый, но объявлять методы
        // вразбивку нельзя — вызов ушёл бы не в тот слот.
        [ComImport]
        [Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface INetworkCostManager
        {
            [PreserveSig]
            int GetCost(out uint pCost, IntPtr pDestIPAddr);

            [PreserveSig]
            int GetDataPlanStatus(IntPtr pDataPlanStatus, IntPtr pDestIPAddr);

            [PreserveSig]
            int SetDestinationAddresses(uint length, IntPtr pDestIPAddrList, [MarshalAs(UnmanagedType.VariantBool)] bool bAppend);
        }
    }
}
