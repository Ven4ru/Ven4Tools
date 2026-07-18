using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Ven4Tools.Shared
{
    /// <summary>
    /// Включает тёмный системный заголовок окна (DWM immersive dark mode), чтобы
    /// нативный title bar не выбивался светлым пятном на тёмной теме приложения —
    /// WPF не подхватывает это из системной тёмной темы сам по себе.
    /// Подписка на уровне класса Window: работает для всех окон обоих приложений,
    /// включая диалоги, без правок в каждом отдельном XAML/code-behind.
    /// </summary>
    public static class WindowChromeHelper
    {
        // 20 — актуальный атрибут (Windows 10 20H1+/11). 19 — фолбэк для более старых сборок.
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeOld = 19;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static void RegisterGlobalDarkTitleBar()
        {
            EventManager.RegisterClassHandler(
                typeof(Window),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnWindowLoaded));
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Window window) return;
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                int useDark = 1;
                if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeOld, ref useDark, sizeof(int));
            }
            catch { /* косметика — сбой не должен ронять окно */ }
        }
    }
}
