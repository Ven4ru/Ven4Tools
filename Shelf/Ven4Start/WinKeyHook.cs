using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ven4Start;

/// <summary>
/// Флаг --win-key: одиночное нажатие-отпускание Win открывает наше меню.
/// WH_KEYBOARD_LL (SetWindowsHookEx). Само нажатие Win мы НЕ глотаем, поэтому любые
/// сочетания Win+X видит Windows как обычно. Перед отпусканием одиночного Win
/// посылаем через SendInput не назначенную клавишу (0xE8) — для оболочки это
/// «Win+что-то», и родной Пуск не открывается. Хук снимается системой сам,
/// если процесс завершится или упадёт.
///
/// Хук живёт в своём потоке со своим циклом сообщений, а не в потоке окна: пока окно занято
/// (построение списка, сборка мусора), каждое нажатие клавиши в системе ждало бы хук, а после
/// LowLevelHooksTimeout Windows молча снимает его — и Win перестал бы открывать меню.
/// </summary>
internal sealed class WinKeyHook : IDisposable
{
    private readonly Native.LowLevelKeyboardProc _proc; // держим ссылку, чтобы GC не собрал делегат
    private readonly Action _onSingleWin;
    private readonly Thread _thread;
    private uint _threadId;
    private IntPtr _hook;
    private bool _winDown;
    private bool _combo;

    public bool Installed => _hook != IntPtr.Zero;

    public WinKeyHook(Action onSingleWin)
    {
        _onSingleWin = onSingleWin;
        _proc = Callback;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
            ready.Set();
            if (_hook == IntPtr.Zero) return;
            // Хук вызывается в этом потоке, пока он ждёт сообщений; WM_QUIT из Dispose завершает цикл.
            while (Native.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "Ven4Start.WinKey" };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
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
        if (_threadId != 0 && _thread.IsAlive)
        {
            Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(1));
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
