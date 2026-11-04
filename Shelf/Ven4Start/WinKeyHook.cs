using System;
using System.Runtime.InteropServices;

namespace Ven4Start;

/// <summary>
/// Флаг --win-key: одиночное нажатие-отпускание Win открывает наше меню.
/// WH_KEYBOARD_LL (SetWindowsHookEx). Само нажатие Win мы НЕ глотаем, поэтому любые
/// сочетания Win+X видит Windows как обычно. Перед отпусканием одиночного Win
/// посылаем через SendInput не назначенную клавишу (0xE8) — для оболочки это
/// «Win+что-то», и родной Пуск не открывается. Хук снимается системой сам,
/// если процесс завершится или упадёт.
/// </summary>
internal sealed class WinKeyHook : IDisposable
{
    private readonly Native.LowLevelKeyboardProc _proc; // держим ссылку, чтобы GC не собрал делегат
    private readonly Action _onSingleWin;
    private IntPtr _hook;
    private bool _winDown;
    private bool _combo;

    public bool Installed => _hook != IntPtr.Zero;

    public WinKeyHook(Action onSingleWin)
    {
        _onSingleWin = onSingleWin;
        _proc = Callback;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            bool injected = (k.flags & Native.LLKHF_INJECTED) != 0;
            int msg = wParam.ToInt32();
            bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
            bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
            bool isWin = k.vkCode == Native.VK_LWIN || k.vkCode == Native.VK_RWIN;

            if (!injected)
            {
                if (isWin && down)
                {
                    if (!_winDown) { _winDown = true; _combo = false; }
                }
                else if (!isWin && down && _winDown)
                {
                    _combo = true; // Win+X — оставляем Windows
                }
                else if (isWin && up && _winDown)
                {
                    _winDown = false;
                    if (!_combo && !FullScreen.IsForegroundFullScreen())
                    {
                        SuppressStart();
                        // Тяжёлую работу в хуке не делаем — только ставим в очередь UI.
                        _onSingleWin();
                    }
                }
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static void SuppressStart()
    {
        var inputs = new Native.INPUT[2];
        inputs[0].type = Native.INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = Native.VK_UNASSIGNED;
        inputs[1].type = Native.INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = Native.VK_UNASSIGNED;
        inputs[1].u.ki.dwFlags = Native.KEYEVENTF_KEYUP;
        Native.SendInput(2, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}

internal static class FullScreen
{
    /// <summary>Полноэкранное приложение/игра/презентация на переднем плане.</summary>
    public static bool IsForegroundFullScreen()
    {
        if (Native.SHQueryUserNotificationState(out int st) == 0 &&
            (st == Native.QUNS_BUSY || st == Native.QUNS_RUNNING_D3D_FULL_SCREEN || st == Native.QUNS_PRESENTATION_MODE))
            return true;

        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Native.GetShellWindow() || fg == Native.GetDesktopWindow())
            return false;
        if (!Native.GetWindowRect(fg, out var r)) return false;

        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST), ref mi)) return false;
        var m = mi.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }
}
