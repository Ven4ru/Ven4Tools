using System;
using System.Runtime.InteropServices;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Запуск программы БЕЗ прав администратора из клиента, который сам всегда работает
    /// с повышенными правами. Запрос уходит в оболочку Windows (explorer.exe рабочего
    /// стола) — программа стартует так же, как если бы пользователь открыл её сам.
    /// </summary>
    /// <remarks>
    /// Нужен для удаления программ, поставленных в профиль пользователя:
    /// winget из-под администратора отказывается их удалять (0x8A15007D,
    /// ADMIN_CONTEXT_ACTION_PROHIBITED), а команду удаления из HKCU клиент с повышенными
    /// правами не запускает намеренно (см. AppUninstallService). Без повышения прав
    /// обе дороги безопасны: процесс получает ровно права пользователя, которому эти
    /// данные и так доступны на запись. Найдено на стенде 16.10.2026.
    /// Способ — IShellDispatch2.ShellExecute у окна рабочего стола (Raymond Chen,
    /// «How can I launch an unelevated process from my elevated process»).
    /// </remarks>
    internal static class UnelevatedLauncher
    {
        /// <returns>false — оболочка недоступна (нет рабочего стола, explorer не запущен).</returns>
        public static bool TryStart(string file, string arguments)
        {
            object? shellWindows = null;
            try
            {
                var type = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
                if (type == null) return false;
                shellWindows = Activator.CreateInstance(type);
                if (shellWindows is not IShellWindows windows) return false;

                object loc = CSIDL_DESKTOP;
                object? empty = null; // VT_EMPTY
                var dispatch = windows.FindWindowSW(ref loc, ref empty, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
                if (dispatch is not IServiceProvider sp) return false;

                Guid sidTop = SID_STopLevelBrowser, iidBrowser = typeof(IShellBrowser).GUID;
                var browser = (IShellBrowser)sp.QueryService(ref sidTop, ref iidBrowser);
                browser.QueryActiveShellView(out var view);
                Guid iidDispatch = IID_IDispatch;
                view.GetItemObject(SVGIO_BACKGROUND, ref iidDispatch, out var bg);
                dynamic folderView = bg;
                dynamic shell = folderView.Application;
                // vShow = 0 (SW_HIDE): удаляторы и winget не нуждаются в окне консоли.
                shell.ShellExecute(file, arguments, "", "open", 0);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[UnelevatedLauncher] Не удалось запустить без прав администратора: {ex.Message}");
                return false;
            }
            finally
            {
                if (shellWindows != null && Marshal.IsComObject(shellWindows))
                    Marshal.FinalReleaseComObject(shellWindows);
            }
        }

        private const int CSIDL_DESKTOP = 0;
        private const int SWC_DESKTOP = 8;
        private const int SWFO_NEEDDISPATCH = 1;
        private const uint SVGIO_BACKGROUND = 0;
        private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
        private static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");

        // Порядок методов — порядок vtable из ShlDisp.h (IShellWindows : IDispatch).
        [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IShellWindows
        {
            int Count { get; }
            [return: MarshalAs(UnmanagedType.IDispatch)] object Item([In] object index);
            [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
            void Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int plCookie);
            void RegisterPending(int lThreadId, [In] ref object pvarloc, [In] ref object pvarlocRoot, int swClass, out int plCookie);
            void Revoke(int lCookie);
            void OnNavigate(int lCookie, [In] ref object pvarLoc);
            void OnActivated(int lCookie, [MarshalAs(UnmanagedType.VariantBool)] bool fActive);
            [return: MarshalAs(UnmanagedType.IDispatch)]
            object FindWindowSW([In] ref object pvarLoc, [In] ref object? pvarLocRoot, int swClass, out int phwnd, int swfwOptions);
        }

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProvider
        {
            [return: MarshalAs(UnmanagedType.IUnknown)]
            object QueryService(ref Guid guidService, ref Guid riid);
        }

        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellBrowser
        {
            void GetWindow(out IntPtr phwnd);
            void ContextSensitiveHelp(bool fEnterMode);
            void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
            void SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
            void RemoveMenusSB(IntPtr hmenuShared);
            void SetStatusTextSB(IntPtr pszStatusText);
            void EnableModelessSB(bool fEnable);
            void TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
            void BrowseObject(IntPtr pidl, uint wFlags);
            void GetViewStateStream(uint grfMode, IntPtr ppStrm);
            void GetControlWindow(uint id, out IntPtr phwnd);
            void SendControlMsg(uint id, uint uMsg, uint wParam, uint lParam, IntPtr pret);
            void QueryActiveShellView(out IShellView ppshv);
            void OnViewWindowActive(IShellView pshv);
            void SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
        }

        [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellView
        {
            void GetWindow(out IntPtr phwnd);
            void ContextSensitiveHelp(bool fEnterMode);
            void TranslateAccelerator(IntPtr pmsg);
            void EnableModeless(bool fEnable);
            void UIActivate(uint uState);
            void Refresh();
            void CreateViewWindow(IShellView psvPrevious, IntPtr pfs, IShellBrowser psb, IntPtr prcView, out IntPtr phWnd);
            void DestroyViewWindow();
            void GetCurrentInfo(IntPtr pfs);
            void AddPropertySheetPages(uint dwReserved, IntPtr pfn, uint lparam);
            void SaveViewState();
            void SelectItem(IntPtr pidlItem, uint uFlags);
            void GetItemObject(uint uItem, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        }
    }
}
