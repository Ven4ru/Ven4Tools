using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Ven4Bar;

internal sealed class TaskWindow
{
    public IntPtr Hwnd { get; init; }
    public string Title { get; set; } = "";
    public ImageSource? Icon { get; set; }
}

/// <summary>
/// Список окон «для панели задач» (EnumWindows + фильтр) и обновление по событиям
/// SetWinEventHook. Таймеров-опросов нет: событие лишь ставит в очередь одно пересчитывание.
/// </summary>
internal sealed class WindowTracker : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly List<IntPtr> _hooks = new();
    private readonly Native.WinEventDelegate _proc; // держим ссылку, чтобы сборщик не убрал делегат
    private readonly int _ownPid = Environment.ProcessId;
    private bool _refreshQueued;

    public event Action<IReadOnlyList<TaskWindow>>? WindowsChanged;
    public event Action<IntPtr>? ForegroundChanged;

    public WindowTracker(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = OnWinEvent;
    }

    public void Start()
    {
        const uint flags = Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS;
        Hook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, flags);
        Hook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND, flags);
        Hook(Native.EVENT_OBJECT_CREATE, Native.EVENT_OBJECT_HIDE, flags); // CREATE, DESTROY, SHOW, HIDE
        Hook(Native.EVENT_OBJECT_NAMECHANGE, Native.EVENT_OBJECT_NAMECHANGE, flags);
        Hook(Native.EVENT_OBJECT_CLOAKED, Native.EVENT_OBJECT_UNCLOAKED, flags);
        QueueRefresh();
    }

    private void Hook(uint min, uint max, uint flags)
    {
        var h = Native.SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0, flags);
        if (h != IntPtr.Zero) _hooks.Add(h);
    }

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Native.OBJID_WINDOW || idChild != Native.CHILDID_SELF || hwnd == IntPtr.Zero) return;

        if (ev == Native.EVENT_SYSTEM_FOREGROUND)
            ForegroundChanged?.Invoke(hwnd);

        QueueRefresh();
    }

    /// <summary>Несколько событий подряд сливаются в одно пересчитывание.</summary>
    public void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _refreshQueued = false;
            WindowsChanged?.Invoke(Enumerate());
        }));
    }

    private readonly Dictionary<IntPtr, ImageSource?> _iconCache = new();

    public List<TaskWindow> Enumerate()
    {
        var list = new List<TaskWindow>();
        var alive = new HashSet<IntPtr>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (IsTaskbarWindow(hwnd))
            {
                alive.Add(hwnd);
                if (!_iconCache.TryGetValue(hwnd, out var icon))
                {
                    icon = GetIcon(hwnd);
                    _iconCache[hwnd] = icon;
                }
                list.Add(new TaskWindow { Hwnd = hwnd, Title = Native.GetTitle(hwnd), Icon = icon });
            }
            return true;
        }, IntPtr.Zero);

        foreach (var key in new List<IntPtr>(_iconCache.Keys))
            if (!alive.Contains(key)) _iconCache.Remove(key);

        // EnumWindows идёт в Z-порядке; для панели стабильнее порядок появления — сортируем по дескриптору
        // не будем: оставляем порядок, но держим его стабильным на стороне UI (см. BarWindow).
        return list;
    }

    /// <summary>Значок мог смениться (например, после NAMECHANGE) — сбросить кэш для окна.</summary>
    public void InvalidateIcon(IntPtr hwnd) => _iconCache.Remove(hwnd);

    public bool IsTaskbarWindow(IntPtr hwnd)
    {
        if (!Native.IsWindowVisible(hwnd)) return false;

        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == _ownPid) return false;

        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return false;

        IntPtr owner = Native.GetWindow(hwnd, Native.GW_OWNER);
        if (owner != IntPtr.Zero && (ex & Native.WS_EX_APPWINDOW) == 0) return false;

        if ((ex & Native.WS_EX_NOACTIVATE) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return false;

        if (Native.IsCloaked(hwnd)) return false;

        if (Native.GetWindowTextLength(hwnd) == 0) return false;

        return true;
    }

    private static ImageSource? GetIcon(IntPtr hwnd)
    {
        IntPtr h = IntPtr.Zero;
        foreach (var kind in new[] { Native.ICON_SMALL2, Native.ICON_SMALL, Native.ICON_BIG })
        {
            Native.SendMessageTimeout(hwnd, Native.WM_GETICON, new IntPtr(kind), IntPtr.Zero,
                Native.SMTO_ABORTIFHUNG, 50, out h);
            if (h != IntPtr.Zero) break;
        }
        if (h == IntPtr.Zero) h = Native.GetClassLongPtr(hwnd, Native.GCLP_HICONSM);
        if (h == IntPtr.Zero) h = Native.GetClassLongPtr(hwnd, Native.GCLP_HICON);
        if (h == IntPtr.Zero) return null;

        try
        {
            // Значок принадлежит чужому окну — не уничтожаем его, а копируем в свой BitmapSource.
            var src = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var h in _hooks) Native.UnhookWinEvent(h);
        _hooks.Clear();
    }
}
