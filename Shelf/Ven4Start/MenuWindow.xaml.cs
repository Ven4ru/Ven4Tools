using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace Ven4Start;

public partial class MenuWindow : Window
{
    private List<AppEntry> _all;
    private bool _refreshing;
    private PowerAction? _pending;

    public MenuWindow()
    {
        InitializeComponent();
        _all = ShellApps.LoadCache();
        ApplyFilter();
        Deactivated += (_, _) => HideMenu();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (Confirm.Visibility == Visibility.Visible) CloseConfirm();
                else HideMenu();
            }
        };
    }

    // ---------- показ / скрытие ----------

    public void ShowMenu()
    {
        Search.Text = string.Empty;
        CloseConfirm();
        ApplyFilter();

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        PlaceAtCursorMonitor(hwnd);

        Show();
        Activate();
        Native.SetForegroundWindow(hwnd);
        Search.Focus();
        Keyboard.Focus(Search);

        RefreshInBackground();
    }

    public void HideMenu()
    {
        if (IsVisible) Hide();
    }

    /// <summary>У нижнего края рабочей области монитора, где сейчас мышь, по центру.</summary>
    private void PlaceAtCursorMonitor(IntPtr hwnd)
    {
        Native.GetCursorPos(out var pt);
        IntPtr mon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return;

        double scale = 1.0;
        if (Native.GetDpiForMonitor(mon, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _) == 0 && dpiX > 0)
            scale = dpiX / 96.0;

        int w = (int)Math.Round(Width * scale);
        int h = (int)Math.Round(Height * scale);
        var wa = mi.rcWork;
        int gap = (int)Math.Round(12 * scale);
        int x = wa.Left + (wa.Right - wa.Left - w) / 2;
        int y = wa.Bottom - h - gap;
        if (y < wa.Top) y = wa.Top;

        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    // ---------- список и поиск ----------

    private async void RefreshInBackground()
    {
        if (_refreshing) return;
        _refreshing = true;
        if (_all.Count == 0) Status.Text = "Загрузка списка программ…";
        try
        {
            var fresh = await ShellApps.EnumerateAsync();
            _all = fresh;
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось прочитать список программ: " + ex.Message;
        }
        finally { _refreshing = false; }
    }

    private void ApplyFilter()
    {
        string q = Search.Text.Trim();
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        IEnumerable<AppEntry> items = _all;
        if (q.Length > 0)
        {
            items = _all
                .Where(a => a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(a => a.Name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase);
        }

        var list = items.ToList();
        Apps.ItemsSource = list;
        if (list.Count > 0) Apps.SelectedIndex = 0;

        if (!_refreshing || _all.Count > 0)
            Status.Text = q.Length == 0 ? $"Все программы ({list.Count})" : $"Найдено: {list.Count}";
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (Apps.SelectedItem is AppEntry sel) LaunchApp(sel);
            else if (Apps.Items.Count > 0 && Apps.Items[0] is AppEntry first) LaunchApp(first);
        }
        else if (e.Key == Key.Down && Apps.Items.Count > 0)
        {
            e.Handled = true;
            Apps.SelectedIndex = Math.Min(Apps.SelectedIndex + 1, Apps.Items.Count - 1);
            Apps.ScrollIntoView(Apps.SelectedItem);
        }
        else if (e.Key == Key.Up && Apps.Items.Count > 0)
        {
            e.Handled = true;
            Apps.SelectedIndex = Math.Max(Apps.SelectedIndex - 1, 0);
            Apps.ScrollIntoView(Apps.SelectedItem);
        }
    }

    private void Apps_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Apps.SelectedItem is AppEntry sel)
        {
            e.Handled = true;
            LaunchApp(sel);
        }
    }

    private void Apps_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Apps.SelectedItem is AppEntry sel) LaunchApp(sel);
    }

    private void LaunchApp(AppEntry app)
    {
        HideMenu();
        if (!ShellApps.Launch(app))
        {
            // Окно уже скрыто — показываем снова с сообщением в строке состояния.
            ShowMenu();
            Status.Text = $"Не удалось запустить «{app.Name}»";
        }
    }

    // ---------- питание ----------

    private void Power_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag || !Enum.TryParse(tag, out PowerAction action)) return;

        if (!Power.NeedsConfirm(action))
        {
            HideMenu();
            Power.Run(action);
            return;
        }

        _pending = action;
        ConfirmText.Text = action switch
        {
            PowerAction.Sleep => "Перевести компьютер в спящий режим?",
            PowerAction.Restart => "Перезагрузить компьютер? Несохранённые данные могут пропасть.",
            PowerAction.Shutdown => "Выключить компьютер? Несохранённые данные могут пропасть.",
            _ => Power.Title(action) + "?",
        };
        Confirm.Visibility = Visibility.Visible;
        ConfirmNo.Focus();
    }

    private void ConfirmYes_Click(object sender, RoutedEventArgs e)
    {
        var a = _pending;
        CloseConfirm();
        HideMenu();
        if (a is { } action && !Power.Run(action))
        {
            ShowMenu();
            Status.Text = $"Не получилось: {Power.Title(action)}";
        }
    }

    private void ConfirmNo_Click(object sender, RoutedEventArgs e)
    {
        CloseConfirm();
        Search.Focus();
    }

    private void CloseConfirm()
    {
        _pending = null;
        Confirm.Visibility = Visibility.Collapsed;
    }
}
