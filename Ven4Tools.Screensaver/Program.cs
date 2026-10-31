using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Ven4Tools.Screensaver
{
    /// <summary>
    /// Точка входа заставки. Windows запускает файл .scr с ключом:
    /// <c>/s</c> — показать заставку, <c>/p &lt;окно&gt;</c> — маленький предпросмотр
    /// в параметрах, <c>/c</c> — настройки (у нас их нет). Ключ бывает и в виде «/p:1234».
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "/c";
            var arg = args.Length > 1 ? args[1] : null;
            if (mode.Length > 2 && mode[2] == ':')
            {
                arg = mode.Substring(3);
                mode = mode.Substring(0, 2);
            }

            switch (mode)
            {
                case "/s":
                    foreach (var screen in Screen.AllScreens)
                        new SaverForm(screen.Bounds).Show();
                    Application.Run();
                    break;

                case "/p":
                    if (long.TryParse(arg, out var handle))
                        Application.Run(new SaverForm(new IntPtr(handle)));
                    break;

                default:
                    MessageBox.Show(
                        "У заставки «Венчик» нет настроек. Время до запуска меняется в параметрах заставки Windows, " +
                        "а вернуть прежнюю заставку можно в Ven4Tools — раздел «Персонализация».",
                        "Венчик", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    break;
            }
        }
    }

    internal static class Native
    {
        [DllImport("user32.dll")]
        public static extern IntPtr SetParent(IntPtr child, IntPtr parent);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect { public int Left, Top, Right, Bottom; }

        public const int GWL_STYLE = -16;
        public const int WS_CHILD = 0x40000000;

        public static Rectangle ClientRect(IntPtr hwnd)
        {
            GetClientRect(hwnd, out var r);
            return new Rectangle(0, 0, r.Right - r.Left, r.Bottom - r.Top);
        }
    }
}
