using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Ven4Bar;

/// <summary>
/// Регистрация окна как панели рабочего стола (appbar) у нижнего края основного монитора.
/// Только SHAppBarMessage, как в документации «Using Application Desktop Toolbars».
/// </summary>
internal sealed class AppBar : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly uint _callbackMsg;
    private int _heightPx;
    private bool _registered;
    private bool _fullscreen;
    private readonly object _lock = new();

    /// <summary>Вызывается, когда поверх появилось/исчезло полноэкранное окно.</summary>
    public event Action<bool>? FullscreenChanged;

    public AppBar(IntPtr hwnd, int heightPx)
    {
        _hwnd = hwnd;
        _heightPx = heightPx;
        _callbackMsg = Native.RegisterWindowMessage("Ven4Bar.AppBarCallback");
    }

    public void Register()
    {
        var abd = NewData();
        abd.uCallbackMessage = _callbackMsg;
        if (Native.SHAppBarMessage(Native.ABM_NEW, ref abd) == UIntPtr.Zero)
            throw new InvalidOperationException("Не удалось зарегистрировать панель (ABM_NEW).");
        _registered = true;
        Reposition();
    }

    public void SetHeight(int heightPx)
    {
        _heightPx = heightPx;
        Reposition();
    }

    /// <summary>ABM_QUERYPOS → поправка → ABM_SETPOS → перемещение своего окна.</summary>
    public void Reposition()
    {
        if (!_registered) return;
        int sw = Native.GetSystemMetrics(Native.SM_CXSCREEN);
        int sh = Native.GetSystemMetrics(Native.SM_CYSCREEN);

        var abd = NewData();
        abd.uEdge = Native.ABE_BOTTOM;
        abd.rc = new Native.RECT { Left = 0, Top = sh - _heightPx, Right = sw, Bottom = sh };
        Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref abd);

        // Система могла сдвинуть нижнюю границу (например, над родной панелью) — держим свою высоту.
        abd.rc.Top = abd.rc.Bottom - _heightPx;
        Native.SHAppBarMessage(Native.ABM_SETPOS, ref abd);

        Native.SetWindowPos(_hwnd, _fullscreen ? Native.HWND_BOTTOM : Native.HWND_TOPMOST, abd.rc.Left, abd.rc.Top,
            abd.rc.Width, abd.rc.Height, Native.SWP_NOACTIVATE);
    }

    /// <summary>Хук оконной процедуры WPF.</summary>
    public IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_registered) return IntPtr.Zero;

        if ((uint)msg == _callbackMsg)
        {
            switch (wParam.ToInt32())
            {
                case Native.ABN_POSCHANGED:
                    Reposition();
                    break;
                case Native.ABN_FULLSCREENAPP:
                    _fullscreen = lParam != IntPtr.Zero;
                    // Сначала даём окну WPF снять/вернуть Topmost, затем сами ставим Z-порядок:
                    // уходим под полноэкранное окно и возвращаемся наверх после него.
                    FullscreenChanged?.Invoke(_fullscreen);
                    Native.SetWindowPos(_hwnd, _fullscreen ? Native.HWND_BOTTOM : Native.HWND_TOPMOST,
                        0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                    break;
            }
            handled = true;
            return IntPtr.Zero;
        }

        switch ((uint)msg)
        {
            case Native.WM_ACTIVATE:
            {
                var abd = NewData();
                Native.SHAppBarMessage(Native.ABM_ACTIVATE, ref abd);
                break;
            }
            case Native.WM_WINDOWPOSCHANGED:
            {
                var abd = NewData();
                Native.SHAppBarMessage(Native.ABM_WINDOWPOSCHANGED, ref abd);
                break;
            }
            case Native.WM_DISPLAYCHANGE:
                Reposition();
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>ABM_REMOVE. Безопасно вызывать много раз и из любого потока.</summary>
    public void Unregister()
    {
        lock (_lock)
        {
            if (!_registered) return;
            _registered = false;
            var abd = NewData();
            Native.SHAppBarMessage(Native.ABM_REMOVE, ref abd);
        }
    }

    public void Dispose() => Unregister();

    private Native.APPBARDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<Native.APPBARDATA>(),
        hWnd = _hwnd,
    };
}
