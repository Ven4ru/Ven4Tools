using System;
using System.Runtime.InteropServices;

namespace Ven4Bar;

/// <summary>
/// Регистрация окна как панели рабочего стола (appbar) у любого края основного монитора — только
/// SHAppBarMessage, как в документации «Using Application Desktop Toolbars».
///  • Обычный режим: ABM_QUERYPOS → ABM_SETPOS — край резервируется, развёрнутые окна под панель не заходят.
///    У нижней панели над рабочей областью добавляется прозрачная полоса под ушки «Пуска» — она не резервируется.
///  • Автоскрытие: ABM_SETAUTOHIDEBAREX — место не резервируется, окно уходит за край, оставляя полоску 2 px,
///    и выезжает при наведении (у любого края — то, чего нет у родной панели Windows 11 26H2).
/// </summary>
internal sealed class AppBar : IDisposable
{
    private const int ABM_SETAUTOHIDEBAREX = 0xC;
    private const int Peek = 2;   // сколько пикселей видно у спрятанной панели

    private readonly IntPtr _hwnd;
    private readonly uint _callbackMsg;
    private int _thickPx, _earsPx;
    private readonly int _edge;
    private readonly bool _autoHide;
    private bool _registered, _autoHideRegistered, _hidden;
    private bool _fullscreen;
    private readonly object _lock = new();

    /// <summary>Вызывается, когда поверх появилось/исчезло полноэкранное окно.</summary>
    public event Action<bool>? FullscreenChanged;

    public bool Hidden => _hidden;

    public AppBar(IntPtr hwnd, int thickPx, int earsPx, int edge, bool autoHide)
    {
        _hwnd = hwnd;
        _thickPx = thickPx;
        _earsPx = earsPx;
        _edge = edge;
        _autoHide = autoHide;
        _callbackMsg = Native.RegisterWindowMessage("Ven4Bar.AppBarCallback");
    }

    public void Register()
    {
        var abd = NewData();
        abd.uCallbackMessage = _callbackMsg;
        if (Native.SHAppBarMessage(Native.ABM_NEW, ref abd) == UIntPtr.Zero)
            throw new InvalidOperationException("Не удалось зарегистрировать панель (ABM_NEW).");
        _registered = true;
        if (_autoHide)
        {
            var ah = NewData();
            ah.uEdge = (uint)_edge;
            ah.rc = Screen();
            ah.lParam = new IntPtr(1);
            // Если у этого края уже есть автоскрываемая панель (например, родная), система откажет — работаем сами.
            _autoHideRegistered = Native.SHAppBarMessage(ABM_SETAUTOHIDEBAREX, ref ah) != UIntPtr.Zero;
            _hidden = false;
        }
        Reposition();
    }

    public void SetThickness(int thickPx, int earsPx)
    {
        _thickPx = thickPx;
        _earsPx = earsPx;
        Reposition();
    }

    /// <summary>Автоскрытие: спрятать за край (true) или показать (false).</summary>
    public void SetHidden(bool hidden)
    {
        if (!_autoHide || _hidden == hidden) return;
        _hidden = hidden;
        Reposition();
    }

    private static Native.RECT Screen() => new()
    {
        Left = 0, Top = 0,
        Right = Native.GetSystemMetrics(Native.SM_CXSCREEN),
        Bottom = Native.GetSystemMetrics(Native.SM_CYSCREEN),
    };

    /// <summary>Место панели и окна по краю (и, при автоскрытии, по тому, спрятана ли она).</summary>
    public void Reposition()
    {
        if (!_registered) return;
        var s = Screen();
        int sw = s.Right, sh = s.Bottom, t = _thickPx;
        Native.RECT rc;
        if (_autoHide)
        {
            int off = _hidden ? t - Peek : 0;
            rc = _edge switch
            {
                Native.ABE_LEFT => new Native.RECT { Left = -off, Top = 0, Right = t - off, Bottom = sh },
                Native.ABE_TOP => new Native.RECT { Left = 0, Top = -off, Right = sw, Bottom = t - off },
                Native.ABE_RIGHT => new Native.RECT { Left = sw - t + off, Top = 0, Right = sw + off, Bottom = sh },
                _ => new Native.RECT { Left = 0, Top = sh - t + off, Right = sw, Bottom = sh + off },
            };
        }
        else
        {
            var abd = NewData();
            abd.uEdge = (uint)_edge;
            abd.rc = _edge switch
            {
                Native.ABE_LEFT => new Native.RECT { Left = 0, Top = 0, Right = t, Bottom = sh },
                Native.ABE_TOP => new Native.RECT { Left = 0, Top = 0, Right = sw, Bottom = t },
                Native.ABE_RIGHT => new Native.RECT { Left = sw - t, Top = 0, Right = sw, Bottom = sh },
                _ => new Native.RECT { Left = 0, Top = sh - t, Right = sw, Bottom = sh },
            };
            Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref abd);
            // Система могла сдвинуть край (например, к родной панели) — держим свою толщину.
            switch (_edge)
            {
                case Native.ABE_LEFT: abd.rc.Right = abd.rc.Left + t; break;
                case Native.ABE_TOP: abd.rc.Bottom = abd.rc.Top + t; break;
                case Native.ABE_RIGHT: abd.rc.Left = abd.rc.Right - t; break;
                default: abd.rc.Top = abd.rc.Bottom - t; break;
            }
            Native.SHAppBarMessage(Native.ABM_SETPOS, ref abd);
            rc = abd.rc;
        }

        int ears = _edge == Native.ABE_BOTTOM ? _earsPx : 0;   // прозрачная полоса под ушки — только у нижней
        Native.SetWindowPos(_hwnd, _fullscreen ? Native.HWND_BOTTOM : Native.HWND_TOPMOST, rc.Left, rc.Top - ears,
            rc.Width, rc.Height + ears, Native.SWP_NOACTIVATE);
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

    /// <summary>ABM_REMOVE (и снятие автоскрытия). Безопасно вызывать много раз и из любого потока.</summary>
    public void Unregister()
    {
        lock (_lock)
        {
            if (!_registered) return;
            _registered = false;
            if (_autoHideRegistered)
            {
                var ah = NewData();
                ah.uEdge = (uint)_edge;
                ah.rc = Screen();
                ah.lParam = IntPtr.Zero;
                Native.SHAppBarMessage(ABM_SETAUTOHIDEBAREX, ref ah);
                _autoHideRegistered = false;
            }
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
