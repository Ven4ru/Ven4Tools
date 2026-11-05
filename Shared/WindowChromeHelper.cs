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
    /// <para>
    /// Диалоги клиента — обычные окна (ResizeMode="NoResize", ShowInTaskbar="False"),
    /// а не WindowStyle="ToolWindow": у окна-инструмента с тёмным заголовком Windows 11
    /// рисует кнопку закрытия красной постоянно, а не только при наведении, как у
    /// остальных окон системы.
    /// </para>
    /// </summary>
    public static class WindowChromeHelper
    {
        // 20 — актуальный атрибут (Windows 10 20H1+/11). 19 — фолбэк для более старых сборок.
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeOld = 19;

        private const int WmNcActivate = 0x0086;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

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
                bool applied = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int)) == 0
                               || DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeOld, ref useDark, sizeof(int)) == 0;

                // Windows 10 (сборки до 22000) сам заголовок по новому атрибуту не
                // перерисовывает: к Loaded окно уже показано со светлой рамкой, и
                // тёмной становилась только полоска под текстом заголовка при его
                // следующей отрисовке — белая рамка с тёмным прямоугольником. Windows 11
                // перерисовывает сам. WM_NCACTIVATE с инверсным, а затем текущим
                // состоянием заставляет DWM перерисовать всю рамку целиком; само
                // окно при этом активным/неактивным не становится (это делает
                // WM_ACTIVATE, а не WM_NCACTIVATE).
                if (applied && Environment.OSVersion.Version.Build < 22000)
                {
                    bool active = GetForegroundWindow() == hwnd;
                    SendMessage(hwnd, WmNcActivate, (IntPtr)(active ? 0 : 1), IntPtr.Zero);
                    SendMessage(hwnd, WmNcActivate, (IntPtr)(active ? 1 : 0), IntPtr.Zero);
                }
            }
            catch { /* косметика — сбой не должен ронять окно */ }
        }
    }
}
