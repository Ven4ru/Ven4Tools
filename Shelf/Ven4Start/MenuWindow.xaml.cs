using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ven4Start;

/// <summary>Программа в меню: название, значок (подгружается в фоне), первая буква — пока значка нет.</summary>
public sealed class AppItem : INotifyPropertyChanged
{
    public AppItem(AppEntry e) { Entry = e; }
    public AppEntry Entry { get; }
    public string Name => Entry.Name;
    public string Initial => Entry.Name.Length > 0 ? char.ToUpperInvariant(Entry.Name[0]).ToString() : "?";
    private ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set { _icon = value; Changed(nameof(Icon)); Changed(nameof(NoIconVisibility)); } }
    public Visibility NoIconVisibility => _icon == null ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public partial class MenuWindow : Window
{
    private List<AppEntry> _all;
    private readonly Dictionary<string, AppItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _pins = new();
    private bool _refreshing;
    private PowerAction? _pending;
    private AppItem? _menuTarget;
    private StartSettings _settings = StartSettings.Load();

    public MenuWindow()
    {
        InitializeComponent();
        _all = ShellApps.LoadCache();
        var name = Environment.UserName;
        UserName.Text = name;
        UserInitial.Text = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?";
        ApplyTheme();
        BuildEars();
        _pins = Pins.Load(_all);
        ApplyFilter();
        Deactivated += (_, _) => HideMenu();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (Confirm.Visibility == Visibility.Visible) CloseConfirm();
                else if (SettingsPanel.Visibility == Visibility.Visible) SettingsPanel.Visibility = Visibility.Collapsed;
                else HideMenu();
            }
        };
    }

    // ---------- стиль ----------

    private void ApplyTheme()
    {
        var t = StartTheme.Current();
        SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        var bg = t.Bg;
        Resources["Bg"] = B(Color.FromArgb(0xF7, bg.R, bg.G, bg.B));
        Resources["Panel"] = B(t.Panel);
        Resources["Hover"] = B(t.Hover);
        Resources["Accent"] = B(StartTheme.C(t.Accent));
        Resources["Inner"] = B(Color.FromArgb(0xE6, StartTheme.C(t.Inner).R, StartTheme.C(t.Inner).G, StartTheme.C(t.Inner).B));
        Resources["Fg"] = B(t.Fg);
        Resources["FgDim"] = B(t.FgDim);
        var acc = StartTheme.C(t.Accent);
        Resources["OnAccent"] = B((0.299 * acc.R + 0.587 * acc.G + 0.114 * acc.B) > 150 ? Color.FromRgb(0x10, 0x14, 0x18) : Colors.White);
    }

    /// <summary>Ушки на верхнем крае меню (те же кривые, что у «Ушек Венчика» над «Пуском»).</summary>
    private void BuildEars()
    {
        double W = Width, earW = 50, earH = 30, inset = 78;
        Geometry Ear(double x0, double y, double tipX, double x1)
        {
            var top = Math.Max(y - (x1 - x0) * .66, .8);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x0, y), true, true);
                c.BezierTo(new Point(x0 + (tipX - x0) * .45, y - (y - top) * .55), new Point(tipX - 1.6, top), new Point(tipX, top), true, false);
                c.BezierTo(new Point(tipX + 1.6, top), new Point(x1 - (x1 - tipX) * .35, top + (y - top) * .25), new Point(x1, y), true, false);
                c.LineTo(new Point(x1, y + 3), true, false); c.LineTo(new Point(x0, y + 3), true, false);
            }
            g.Freeze();
            return g;
        }
        // одно ухо — 22×15 условных единиц; левое и правое зеркальны
        Transform T(double left, bool mirror) => new MatrixTransform(mirror ? -earW / 22 : earW / 22, 0, 0, earH / 15.5, mirror ? left + earW : left, 0);
        var outer = Ear(0, 15.5, 11.5, 21.5); var inner = Ear(5.2, 15.5, 12.6, 19.2);
        EarL.Data = outer; EarL.RenderTransform = T(inset, false);
        EarR.Data = outer; EarR.RenderTransform = T(W - inset - earW, true);
        EarLIn.Data = inner; EarLIn.RenderTransform = T(inset, false);
        EarRIn.Data = inner; EarRIn.RenderTransform = T(W - inset - earW, true);
    }

    // ---------- показ / скрытие ----------

    public void ShowMenu()
    {
        ApplyTheme();
        ApplySettings();
        Search.Text = string.Empty;
        CloseConfirm();
        ApplyFilter();

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        PlaceAboveTaskbar(hwnd);

        Show();
        Activate();
        Native.SetForegroundWindow(hwnd);
        Search.Focus();
        Keyboard.Focus(Search);
        if (SystemParameters.ClientAreaAnimation)
        {
            Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        }

        RefreshInBackground();
    }

    public void HideMenu()
    {
        if (IsVisible) Hide();
    }

    /// <summary>Над панелью задач монитора, где мышь: по центру, а если значки слева — у левого края, как родной Пуск.</summary>
    private void PlaceAboveTaskbar(IntPtr hwnd)
    {
        Native.GetCursorPos(out var pt);
        IntPtr mon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return;

        double scale = 1.0;
        if (Native.GetDpiForMonitor(mon, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _) == 0 && dpiX > 0)
            scale = dpiX / 96.0;

        var wa = mi.rcWork;
        var anchor = TakeAnchor();
        double hMax = (wa.Bottom - wa.Top) / scale - 24;
        if (Height > hMax) Height = Math.Max(420, hMax);
        int w = (int)Math.Round(Width * scale);
        int h = (int)Math.Round(Height * scale);
        int gap = (int)Math.Round(10 * scale);
        int x = StartTheme.StartAtLeft() ? wa.Left + gap : wa.Left + (wa.Right - wa.Left - w) / 2;
        int y = wa.Bottom - h - gap;
        if (anchor is { } a)
        {
            if (a.Left) { x = wa.Left + gap; y = wa.Top + gap; }                       // вертикальная панель слева
            else x = Math.Max(wa.Left + gap, Math.Min(a.X - w / 2, wa.Right - w - gap)); // над её «Пуском»
        }
        if (y < wa.Top) y = wa.Top;

        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private static readonly string AnchorFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Start", "anchor.txt");

    /// <summary>Запомнить, где стоит «Пуск» своей панели (передаёт Ven4Bar при щелчке).</summary>
    public static void SaveAnchor(string value)
    {
        try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(AnchorFile)!); System.IO.File.WriteAllText(AnchorFile, value); } catch { }
    }

    /// <summary>Свежая (не старше 5 с) точка «Пуска» Ven4Bar; после чтения файл удаляется.</summary>
    private static (int X, int Y, bool Left)? TakeAnchor()
    {
        try
        {
            if (!System.IO.File.Exists(AnchorFile)) return null;
            var fresh = DateTime.Now - System.IO.File.GetLastWriteTime(AnchorFile) < TimeSpan.FromSeconds(5);
            var parts = System.IO.File.ReadAllText(AnchorFile).Trim().Split(',');
            System.IO.File.Delete(AnchorFile);
            if (!fresh || parts.Length < 2 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)) return null;
            return (x, y, parts.Length > 2 && parts[2] == "left");
        }
        catch { return null; }
    }

    // ---------- список и поиск ----------

    private AppItem Item(AppEntry e)
    {
        if (!_items.TryGetValue(e.ParsingName, out var it))
        {
            it = new AppItem(e) { Icon = Icons.TryGet(e.ParsingName) };
            _items[e.ParsingName] = it;
            if (it.Icon == null) Icons.Request(e.ParsingName, 48, img => it.Icon = img, Dispatcher);
        }
        return it;
    }

    private async void RefreshInBackground()
    {
        if (_refreshing) return;
        _refreshing = true;
        if (_all.Count == 0) Status.Text = "Загрузка списка программ…";
        try
        {
            var fresh = await ShellApps.EnumerateAsync();
            _all = fresh;
            if (_pins.Count == 0) _pins = Pins.Load(_all);
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
        PinnedPanel.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var byId = _all.ToDictionary(a => a.ParsingName, StringComparer.OrdinalIgnoreCase);
        var m = _settings.Metrics;
        Pinned.ItemsSource = _pins.Where(byId.ContainsKey).Take(m.MaxPins).Select(p => Item(byId[p])).ToList();
        PinnedPanel.Visibility = q.Length == 0 && _settings.ShowPinned ? Visibility.Visible : Visibility.Collapsed;
        AllPanel.Visibility = q.Length > 0 || _settings.ShowAll ? Visibility.Visible : Visibility.Collapsed;

        IEnumerable<AppEntry> items = _all;
        if (q.Length > 0)
        {
            items = _all
                .Where(a => a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(a => a.Name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase);
        }

        var list = items.Select(Item).ToList();
        Apps.ItemsSource = list;
        if (list.Count > 0) Apps.SelectedIndex = 0;

        if (!_refreshing || _all.Count > 0)
            Status.Text = q.Length == 0 ? $"Все программы · {list.Count}" : (list.Count > 0 ? $"Найдено: {list.Count}" : "Ничего не нашлось");
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (Apps.SelectedItem is AppItem sel) LaunchApp(sel.Entry);
            else if (Apps.Items.Count > 0 && Apps.Items[0] is AppItem first) LaunchApp(first.Entry);
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
        if (e.Key == Key.Enter && Apps.SelectedItem is AppItem sel)
        {
            e.Handled = true;
            LaunchApp(sel.Entry);
        }
    }

    private void Apps_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Apps.SelectedItem is AppItem sel) LaunchApp(sel.Entry);
    }

    // Как в родном Пуске — один щелчок запускает.
    private void Apps_Click(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject) is { } d && ItemsControl.ContainerFromElement(Apps, d) is ListBoxItem { DataContext: AppItem it })
            LaunchApp(it.Entry);
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AppItem it) LaunchApp(it.Entry);
    }

    private void LaunchApp(AppEntry app)
    {
        HideMenu();
        if (!ShellApps.Launch(app))
        {
            ShowMenu();
            Status.Text = $"Не удалось запустить «{app.Name}»";
        }
    }

    // ---------- настройки меню ----------

    private void ApplySettings()
    {
        var m = _settings.Metrics;
        Width = m.W; Height = m.H;
        Pinned.Tag = m.Cols;
        BuildEars();
        OptPinned.IsChecked = _settings.ShowPinned;
        OptAll.IsChecked = _settings.ShowAll;
        foreach (var (b, i) in new[] { (SizeS, 0), (SizeM, 1), (SizeL, 2) })
            b.Background = (System.Windows.Media.Brush)Resources[_settings.Size == i ? "Hover" : "Panel"];
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ApplySettings();
        SettingsPanel.Visibility = Visibility.Visible;
    }

    private void Size_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string t || !int.TryParse(t, out var size)) return;
        _settings.Size = size; _settings.Save();
        ApplySettings();
        PlaceAboveTaskbar(new WindowInteropHelper(this).Handle);
        ApplyFilter();
    }

    private void Section_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowPinned = OptPinned.IsChecked == true;
        _settings.ShowAll = OptAll.IsChecked == true;
        _settings.Save();
        ApplyFilter();
    }

    private void SettingsDone_Click(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        Search.Focus();
    }

    // ---------- закрепление ----------

    private void AppMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        _menuTarget = (sender as FrameworkElement)?.DataContext as AppItem;
        if (_menuTarget == null) { e.Handled = true; return; }
        if ((sender as FrameworkElement)?.ContextMenu is ContextMenu cm && cm.Items.Count > 1 && cm.Items[1] is MenuItem pin)
            pin.Header = _pins.Contains(_menuTarget.Entry.ParsingName) ? "Открепить" : "Закрепить в начале";
    }

    private void MenuOpen_Click(object sender, RoutedEventArgs e) { if (_menuTarget != null) LaunchApp(_menuTarget.Entry); }

    private void MenuPin_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget == null) return;
        var id = _menuTarget.Entry.ParsingName;
        if (!_pins.Remove(id)) _pins.Add(id);
        Pins.Save(_pins);
        ApplyFilter();
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
