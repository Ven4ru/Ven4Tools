using System;
using System.Windows.Interop;

namespace Ven4Start;

/// <summary>
/// Ctrl+Alt+Space через RegisterHotKey. Окно-приёмник — message-only (HWND_MESSAGE),
/// никаких таймеров: ждём WM_HOTKEY.
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x5634; // «V4»
    private readonly HwndSource _source;
    private readonly Action _onHotkey;
    public bool Registered { get; }

    public HotkeyService(Action onHotkey)
    {
        _onHotkey = onHotkey;
        var p = new HwndSourceParameters("Ven4Start.Hotkey")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        Registered = Native.RegisterHotKey(_source.Handle, HotkeyId,
            Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_SPACE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            _onHotkey();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (Registered) Native.UnregisterHotKey(_source.Handle, HotkeyId);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
