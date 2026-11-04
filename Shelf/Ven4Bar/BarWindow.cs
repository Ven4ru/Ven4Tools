using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace Ven4Bar;

/// <summary>
/// Сама панель (дизайн 04.11.2026): цвета — из темы панели Ven4Tools, «Пуск» — голова Венчика с ушками,
/// окна — значками (по желанию с подписями) с полоской акцента под активным, справа «^», язык, громкость, часы.
/// Настройки (BarSettings): любой край, три размера, стили «плотный / стекло / прозрачный / остров», автоскрытие.
/// </summary>
internal sealed class BarWindow : Window
{
    public const double EarsDip = 13;           // над нижней панелью торчат ушки «Пуска» — прозрачная полоса окна
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    private readonly bool _hideNative;
    private readonly BarSettings _s;
    private readonly bool _vert, _island;
    private readonly BarTheme _theme = BarTheme.Current();
    private readonly Brush _barBg, _barSolid, _hover, _accent, _fg, _fgDim, _active;
    private AppBar? _appBar;
    private WindowTracker? _tracker;
    private readonly Volume _volume = new();
    private readonly ThumbnailPopup _thumb = new();
    private readonly DispatcherTimer _clockTimer = new();
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _dragTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    private readonly StackPanel _tasksPanel = new();
    private readonly Dictionary<IntPtr, Button> _buttons = new();
    private readonly TextBlock _clockTime = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _clockDate = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _volumeIcon = new() { FontFamily = new FontFamily(IconFont), FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _langText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private Button? _hoverButton, _dragButton, _startButton;
    private IntPtr _foreground, _ownHwnd;
    private bool _nativeShownTemporarily;

    public event Action? ExitRequested;
    /// <summary>Настройки изменены — перезапустить панель с ними.</summary>
    public event Action? RestartRequested;

    private static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public BarWindow(bool hideNative, BarSettings settings)
    {
        _hideNative = hideNative;
        _s = settings;
        _vert = _s.Vertical;
        _island = _s.Style == "island" && !_vert;
        var bar = BarTheme.C(_theme.Bar);
        byte alpha = _s.Style switch { "glass" => 0xA8, "clear" => 0x10, _ => 0xF2 };
        _barBg = B(Color.FromArgb(alpha, bar.R, bar.G, bar.B));
        _barSolid = B(Color.FromArgb(0xFF, bar.R, bar.G, bar.B));
        _hover = B(_theme.Hover);
        _active = B(Color.FromArgb(_s.Style == "clear" ? (byte)0x90 : (byte)0xFF, _theme.Panel.R, _theme.Panel.G, _theme.Panel.B));
        _accent = B(BarTheme.C(_theme.Accent));
        _fg = B(_theme.Fg);
        _fgDim = B(_theme.FgDim);
        foreach (var t in new[] { _clockTime, _clockDate, _volumeIcon, _langText }) t.Foreground = _fg;

        Title = "Ven4Bar";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        // Прозрачное окно: у нижней панели сверху полоса под ушки, у «острова» — поля вокруг; щелчки по пустому проходят насквозь.
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        var t0 = _s.Thick;
        if (_vert) { Width = t0; Height = SystemParameters.PrimaryScreenHeight; Left = _s.Edge == BarSettings.Right ? SystemParameters.PrimaryScreenWidth - t0 : 0; Top = 0; }
        else { Height = t0 + (_s.Edge == BarSettings.Bottom ? EarsDip : 0); Width = SystemParameters.PrimaryScreenWidth; Left = 0; Top = _s.Edge == BarSettings.Top ? 0 : SystemParameters.PrimaryScreenHeight - Height; }

        Content = BuildLayout();
        ContextMenu = BuildContextMenu();

        SourceInitialized += OnSourceInitialized;
        // Мягкое закрытие (WM_CLOSE, «Завершить задачу» без /F, выход из сеанса) — уборка и выход из программы.
        Closed += (_, _) => { Cleanup(); ExitRequested?.Invoke(); };

        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); ShowThumbForHover(); };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (!IsMouseOver && ContextMenu?.IsOpen != true) _appBar?.SetHidden(true);
        };
        _dragTimer.Tick += (_, _) => { _dragTimer.Stop(); if (_dragButton?.Tag is IntPtr h) Activate(h); };
        if (_s.AutoHide)
        {
            MouseEnter += (_, _) => { _hideTimer.Stop(); _appBar?.SetHidden(false); };
            MouseLeave += (_, _) => { _hideTimer.Stop(); _hideTimer.Start(); };
            DragEnter += (_, _) => _appBar?.SetHidden(false);
            AllowDrop = true;
        }
    }

    // ---------------- Разметка ----------------

    private UIElement BuildLayout()
    {
        var root = new Grid();
        double ears = _s.Edge == BarSettings.Bottom ? EarsDip : 0;
        var body = new Border
        {
            Background = _barBg,
            BorderBrush = B(Color.FromArgb(_s.Style == "clear" ? (byte)0 : (byte)0x22, 0xFF, 0xFF, 0xFF)),
            BorderThickness = _island ? new Thickness(1) : _s.Edge switch
            {
                BarSettings.Left => new Thickness(0, 0, 1, 0),
                BarSettings.Right => new Thickness(1, 0, 0, 0),
                BarSettings.Top => new Thickness(0, 0, 0, 1),
                _ => new Thickness(0, 1, 0, 0),
            },
            CornerRadius = _island ? new CornerRadius(14) : new CornerRadius(0),
            Margin = _island ? new Thickness(0, ears + 4, 0, 4) : new Thickness(0, ears, 0, 0),
            HorizontalAlignment = _island ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
        };
        root.Children.Add(body);

        var start = MakeButton(StartHead(), "Пуск");
        start.Width = _s.Button; start.Height = _s.Button - 4;
        start.Click += (_, _) => OnStart();
        _startButton = start;

        _tasksPanel.Orientation = _vert ? Orientation.Vertical : Orientation.Horizontal;
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = _vert ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = _vert ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled,
            Content = _tasksPanel,
        };
        scroll.PreviewMouseWheel += (_, e) =>
        {
            if (_vert) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta / 2.0);
            else scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta / 2.0);
            e.Handled = true;
        };

        var tray = new StackPanel { Orientation = _vert ? Orientation.Vertical : Orientation.Horizontal };
        string chevron = _s.Edge switch { BarSettings.Left => "", BarSettings.Right => "", BarSettings.Top => "", _ => "" };
        var overflow = MakeButton(new TextBlock { Text = chevron, FontFamily = new FontFamily(IconFont), FontSize = 11, Foreground = _fg },
            "Показать скрытые значки (родная панель задач)");
        overflow.Width = 32; overflow.Height = 36;
        overflow.Click += (_, _) => OnOverflow();
        tray.Children.Add(overflow);

        var lang = MakeButton(_langText, "Язык ввода");
        lang.Width = 40; lang.Height = 36;
        lang.Click += (_, _) => UpdateLanguage(Native.GetForegroundWindow());
        tray.Children.Add(lang);

        var vol = MakeButton(_volumeIcon, "Громкость: колесо мыши — тише/громче, щелчок — выключить звук");
        vol.Width = 40; vol.Height = 36;
        vol.Click += (_, _) => { if (_volume.Available) _volume.Muted = !_volume.Muted; UpdateVolumeIcon(); };
        vol.MouseWheel += (_, e) =>
        {
            if (!_volume.Available) return;
            _volume.Level += e.Delta > 0 ? 0.02f : -0.02f;
            UpdateVolumeIcon();
            e.Handled = true;
        };
        tray.Children.Add(vol);

        var clockStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        clockStack.Children.Add(_clockTime);
        clockStack.Children.Add(_clockDate);
        if (_vert || _s.Size == 0 || _island) _clockDate.Visibility = Visibility.Collapsed;   // в «острове» и маленькой — только время
        var clock = MakeButton(clockStack, "Дата и время");
        clock.Width = _vert ? _s.Thick - 4 : 84; clock.Height = 40;
        tray.Children.Add(clock);

        var grid = new Grid { Margin = _vert ? new Thickness(0, 8, 0, 8) : new Thickness(8, ears, 8, 0) };
        if (_vert)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            start.Margin = new Thickness(2, 0, 2, 6);
            Grid.SetRow(start, 0); Grid.SetRow(scroll, 1); Grid.SetRow(tray, 2);
            grid.Children.Add(start); grid.Children.Add(scroll); grid.Children.Add(tray);
            root.Children.Add(grid);
        }
        else if (_island)
        {
            // «Остров»: всё в одной капсуле посередине, остальное — прозрачно и пропускает щелчки
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 0) };
            row.Children.Add(start); row.Children.Add(scroll);
            row.Children.Add(new Border { Width = 1, Margin = new Thickness(8, 10, 8, 10), Background = _fgDim, Opacity = .4 });
            row.Children.Add(tray);
            body.Child = row;
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // «Пуск» и окна — по центру, как в Windows 11
            var center = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            center.Children.Add(start);
            center.Children.Add(scroll);
            Grid.SetColumn(center, 0); Grid.SetColumn(tray, 1);
            grid.Children.Add(center); grid.Children.Add(tray);
            root.Children.Add(grid);
        }

        // ушки над «Пуском» (у нижней панели) — растут из её верхнего края
        if (_s.Edge == BarSettings.Bottom)
        {
            var e = Ears(_s.Style == "solid" || _island ? _barSolid : _barBg, B(BarTheme.C(_theme.Inner)));
            e.HorizontalAlignment = HorizontalAlignment.Left;
            e.VerticalAlignment = VerticalAlignment.Top;
            root.Children.Add(e);
            // Место считается, когда кнопка уже в дереве и измерена: Loaded приходит после первого прохода
            // разметки, и одного LayoutUpdated до него мало — ушки оставались в левом углу.
            void PlaceEars()
            {
                if (!start.IsLoaded || !root.IsLoaded || start.ActualWidth <= 0) return;
                var p = start.TranslatePoint(new Point(start.ActualWidth / 2, 0), root);
                var m = new Thickness(Math.Round(p.X - e.Width / 2), _island ? 4 : 0, 0, 0);
                if (e.Margin != m) e.Margin = m;
            }
            start.Loaded += (_, _) => Dispatcher.BeginInvoke(PlaceEars, DispatcherPriority.Loaded);
            start.LayoutUpdated += (_, _) => PlaceEars();
            start.SizeChanged += (_, _) => PlaceEars();
            root.SizeChanged += (_, _) => PlaceEars();
        }
        return root;
    }

    private static System.Windows.Media.Imaging.BitmapImage? _face;

    /// <summary>«Пуск» — оригинальная морда Венчика; если картинка не загрузилась — нарисованная голова.</summary>
    private UIElement StartHead()
    {
        try
        {
            if (_face == null)
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri("pack://application:,,,/Ven4Bar;component/Resources/venchik-face.png");
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.EndInit();
                bi.Freeze();
                _face = bi;
            }
            double side = _s.Icon + 6;   // у морды поля прозрачные — чуть крупнее значков окон
            var img = new Image { Source = _face, Width = side, Height = side, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }
        catch (Exception) { return DrawnHead(); }
    }

    private UIElement DrawnHead()
    {
        double k = _s.Icon / 24.0;
        var c = new Canvas { Width = 26 * k, Height = 24 * k };
        var inner = new Canvas { Width = 26, Height = 24, RenderTransform = new ScaleTransform(k, k) };
        Geometry G(string s) { var g = Geometry.Parse(s); g.Freeze(); return g; }
        var bar = B(BarTheme.C(_theme.Bar));
        inner.Children.Add(new Path { Data = G("M1.5,11 L4.5,0.8 L11,7 Z"), Fill = _accent });
        inner.Children.Add(new Path { Data = G("M24.5,11 L21.5,0.8 L15,7 Z"), Fill = _accent });
        var head = new Ellipse { Width = 22, Height = 19, Fill = _accent };
        Canvas.SetLeft(head, 2); Canvas.SetTop(head, 4.5); inner.Children.Add(head);
        foreach (var x in new[] { 7.5, 15.5 })
        {
            var eye = new Ellipse { Width = 3.2, Height = 4.2, Fill = bar };
            Canvas.SetLeft(eye, x); Canvas.SetTop(eye, 11.5); inner.Children.Add(eye);
        }
        c.Children.Add(inner);
        return c;
    }

    /// <summary>Ушки 50×13 (те же кривые, что у «Ушек Венчика» над родным «Пуском»).</summary>
    private static Canvas Ears(Brush outer, Brush inner)
    {
        Geometry Ear(double x0, double y, double tipX, double x1)
        {
            var top = Math.Max(y - (x1 - x0) * .66, .8);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(x0, y), true, true);
                ctx.BezierTo(new Point(x0 + (tipX - x0) * .45, y - (y - top) * .55), new Point(tipX - 1.6, top), new Point(tipX, top), true, false);
                ctx.BezierTo(new Point(tipX + 1.6, top), new Point(x1 - (x1 - tipX) * .35, top + (y - top) * .25), new Point(x1, y), true, false);
                ctx.LineTo(new Point(x1, y + 3), true, false); ctx.LineTo(new Point(x0, y + 3), true, false);
            }
            g.Freeze();
            return g;
        }
        var cv = new Canvas { Width = 50, Height = EarsDip + 2, IsHitTestVisible = false };
        var t = new ScaleTransform(50 / 62.0, (EarsDip + 1) / 15.5);
        Path P(Geometry g, Brush b) => new() { Data = g, Fill = b, RenderTransform = t };
        cv.Children.Add(P(Ear(1, 15.5, 12.5, 22.5), outer));
        cv.Children.Add(P(Ear(39.5, 15.5, 49.5, 61), outer));
        cv.Children.Add(P(Ear(6.2, 15.5, 13.6, 20.2), inner));
        cv.Children.Add(P(Ear(41.8, 15.5, 48.4, 55.8), inner));
        return cv;
    }

    private static void AutomationProps(FrameworkElement e, string name) => System.Windows.Automation.AutomationProperties.SetName(e, name);

    /// <summary>Правый щелчок: то, чего не хватает в меню родной панели Windows 11, и все настройки Ven4Bar.</summary>
    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action a) { var i = new MenuItem { Header = header }; i.Click += (_, _) => a(); return i; }
        MenuItem Choice(string header, bool on, Action a) { var i = new MenuItem { Header = header, IsCheckable = true, IsChecked = on }; i.Click += (_, _) => a(); return i; }
        void Set(Action<BarSettings> change) { change(_s); _s.Save(); RestartRequested?.Invoke(); }

        menu.Items.Add(Item("Диспетчер задач", () => Start("taskmgr.exe")));
        menu.Items.Add(Item("Показать рабочий стол", () => Native.SendChord(Native.VK_LWIN, Native.VK_D)));
        menu.Items.Add(Item("Параметры панели задач Windows", () => Start("ms-settings:taskbar")));
        menu.Items.Add(new Separator());

        var edge = new MenuItem { Header = "Положение" };
        foreach (var (n, v) in new[] { ("Снизу", BarSettings.Bottom), ("Сверху", BarSettings.Top), ("Слева", BarSettings.Left), ("Справа", BarSettings.Right) })
            edge.Items.Add(Choice(n, _s.Edge == v, () => Set(s => s.Edge = v)));
        menu.Items.Add(edge);
        var size = new MenuItem { Header = "Размер" };
        foreach (var (n, v) in new[] { ("Маленькая", 0), ("Обычная", 1), ("Большая", 2) })
            size.Items.Add(Choice(n, _s.Size == v, () => Set(s => s.Size = v)));
        menu.Items.Add(size);
        var style = new MenuItem { Header = "Вид" };
        foreach (var (n, v) in new[] { ("Плотная", "solid"), ("Стекло", "glass"), ("Прозрачная", "clear"), ("Остров посередине", "island") })
            style.Items.Add(Choice(n, _s.Style == v, () => Set(s => s.Style = v)));
        menu.Items.Add(style);
        menu.Items.Add(Choice("Подписи окон", _s.Labels, () => Set(s => s.Labels = !s.Labels)));
        menu.Items.Add(Choice("Автоматически скрывать", _s.AutoHide, () => Set(s => s.AutoHide = !s.AutoHide)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Выход", () => ExitRequested?.Invoke()));
        menu.Closed += (_, _) => { if (_s.AutoHide && !IsMouseOver) _hideTimer.Start(); };
        return menu;
    }

    private static void Start(string what)
    {
        try { Process.Start(new ProcessStartInfo(what) { UseShellExecute = true }); }
        catch (Exception e) { Debug.WriteLine(e); }
    }

    private Button MakeButton(object content, string tip)
    {
        var b = new Button
        {
            Content = content,
            ToolTip = tip,
            Background = Brushes.Transparent,
            Foreground = _fg,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(2, 4, 2, 4),
            Padding = new Thickness(4, 0, 4, 0),
            Focusable = false,
            Template = FlatTemplate(_hover),
        };
        AutomationProps(b, tip);
        return b;
    }

    /// <summary>Плоский шаблон кнопки: фон из Background, подсветка при наведении.</summary>
    private static ControlTemplate FlatTemplate(Brush hover)
    {
        var t = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding")
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        t.VisualTree = border;
        var h = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        h.Setters.Add(new Setter(Border.BackgroundProperty, hover, "bd"));
        t.Triggers.Add(h);
        return t;
    }

    // ---------------- Запуск ----------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // Своё окно — инструментальное: не попадает в Alt+Tab и в свой же список.
        // И неактивируемое (WS_EX_NOACTIVATE), как родная панель: щелчок по ней не забирает передний план,
        // поэтому повторный щелчок по кнопке активного окна сворачивает его, а не «разворачивает снова».
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        _ownHwnd = hwnd;

        var src = HwndSource.FromHwnd(hwnd);
        _appBar = new AppBar(hwnd, DipToPx(_s.Thick), DipToPx(_s.Edge == BarSettings.Bottom ? EarsDip : 0), _s.Edge, _s.AutoHide);
        src.AddHook(_appBar.WndProc);
        src.AddHook(OwnWndProc);
        _appBar.FullscreenChanged += fs => { Topmost = !fs; if (fs) _thumb.HidePopup(); };
        _appBar.Register();
        if (_s.AutoHide) _hideTimer.Start();   // сначала показаться, потом уйти за край

        if (_hideNative) NativeTaskbar.EnableAutoHide();

        _tracker = new WindowTracker(Dispatcher);
        _tracker.WindowsChanged += OnWindowsChanged;
        _tracker.ForegroundChanged += h => Dispatcher.BeginInvoke(() =>
        {
            // окна самой панели (она сама, миниатюра) «активным окном» не считаются
            if (h == IntPtr.Zero || h == _ownHwnd) return;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == Environment.ProcessId) return;
            _foreground = h;
            _tracker?.InvalidateIcon(h);
            UpdateActive();
            UpdateLanguage(h);
        });
        _tracker.Start();

        if (_volume.Init())
            _volume.Changed += (_, _) => Dispatcher.BeginInvoke(UpdateVolumeIcon);
        UpdateVolumeIcon();

        UpdateLanguage(Native.GetForegroundWindow());
        StartClock();
    }

    private IntPtr OwnWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == Native.WM_DPICHANGED)
            Dispatcher.BeginInvoke(() => _appBar?.SetThickness(DipToPx(_s.Thick), DipToPx(_s.Edge == BarSettings.Bottom ? EarsDip : 0)));
        return IntPtr.Zero;
    }

    private int DipToPx(double dip)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        return (int)Math.Round(dip * scale);
    }

    // ---------------- Часы ----------------

    private void StartClock()
    {
        _clockTimer.Tick += (_, _) => { UpdateClock(); ScheduleNextMinute(); };
        UpdateClock();
        ScheduleNextMinute();
        _clockTimer.Start();
    }

    /// <summary>Следующий тик — ровно в начале следующей минуты (+50 мс запаса).</summary>
    private void ScheduleNextMinute()
    {
        var now = DateTime.Now;
        var next = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0).AddMinutes(1);
        _clockTimer.Interval = next - now + TimeSpan.FromMilliseconds(50);
    }

    private void UpdateClock()
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var now = DateTime.Now;
        _clockTime.Text = now.ToString("HH:mm", ru);
        _clockDate.Text = now.ToString("dd.MM.yyyy", ru);
        if (_clockTime.Parent is FrameworkElement fe && fe.Parent is Button b)
            b.ToolTip = now.ToString("dddd, d MMMM yyyy", ru);
        // Раскладка может смениться без смены окна — заодно освежаем её раз в минуту.
        UpdateLanguage(_foreground != IntPtr.Zero ? _foreground : Native.GetForegroundWindow());
    }

    // ---------------- Громкость и язык ----------------

    private void UpdateVolumeIcon()
    {
        if (!_volume.Available)
        {
            _volumeIcon.Text = "";
            _volumeIcon.ToolTip = "Устройство вывода звука не найдено";
            return;
        }
        float v = _volume.Level;
        bool m = _volume.Muted;
        _volumeIcon.Text = m ? "" : v < 0.01f ? "" : v < 0.34f ? "" : v < 0.67f ? "" : "";
        _volumeIcon.ToolTip = m ? "Звук выключен" : $"Громкость: {Math.Round(v * 100)} %";
    }

    private void UpdateLanguage(IntPtr hwnd)
    {
        uint thread = hwnd != IntPtr.Zero ? Native.GetWindowThreadProcessId(hwnd, out _) : 0;
        int langId = (int)(Native.GetKeyboardLayout(thread).ToInt64() & 0xFFFF);
        string text;
        try
        {
            var ci = CultureInfo.GetCultureInfo(langId);
            text = ci.TwoLetterISOLanguageName.ToUpperInvariant();
            _langText.ToolTip = ci.NativeName;
        }
        catch
        {
            text = "??";
        }
        // Если на переднем плане наша панель — раскладку не меняем, чтобы не мигала.
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId && !string.IsNullOrEmpty(_langText.Text)) return;
        _langText.Text = text;
    }

    // ---------------- Кнопки окон ----------------

    private void OnWindowsChanged(IReadOnlyList<TaskWindow> windows)
    {
        var alive = new HashSet<IntPtr>(windows.Select(w => w.Hwnd));

        foreach (var gone in _buttons.Keys.Where(h => !alive.Contains(h)).ToList())
        {
            _tasksPanel.Children.Remove(_buttons[gone]);
            _buttons.Remove(gone);
        }

        // Новые окна добавляем в конец — порядок кнопок стабилен, как у родной панели.
        foreach (var w in windows.Reverse())
        {
            if (!_buttons.TryGetValue(w.Hwnd, out var btn))
            {
                btn = CreateTaskButton(w.Hwnd);
                _buttons[w.Hwnd] = btn;
                _tasksPanel.Children.Add(btn);
            }
            SetTaskButtonContent(btn, w);
        }
        UpdateActive();
    }

    private Button CreateTaskButton(IntPtr hwnd)
    {
        var b = MakeButton(null!, "");
        b.Tag = hwnd;
        b.Height = _s.Button - 4;
        if (_s.Labels && !_vert) { b.MinWidth = _s.Button; b.MaxWidth = 190; b.HorizontalContentAlignment = HorizontalAlignment.Left; }
        else b.Width = _s.Button;
        b.Click += (_, _) => OnTaskClick(hwnd);
        b.MouseEnter += (_, _) => { _hoverButton = b; _hoverTimer.Stop(); _hoverTimer.Start(); };
        b.MouseLeave += (_, _) => { if (_hoverButton == b) _hoverButton = null; _hoverTimer.Stop(); _thumb.HidePopup(); };
        b.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) Close(hwnd); };
        // Перетаскивание файла: задержались над кнопкой — окно выходит вперёд, файл можно бросить в него.
        b.AllowDrop = true;
        b.DragEnter += (_, e) => { _dragButton = b; _dragTimer.Stop(); _dragTimer.Start(); e.Effects = DragDropEffects.None; e.Handled = true; };
        b.DragOver += (_, e) => { e.Effects = DragDropEffects.None; e.Handled = true; };
        b.DragLeave += (_, _) => { if (_dragButton == b) { _dragButton = null; _dragTimer.Stop(); } };
        return b;
    }

    /// <summary>Кнопка окна — значок (и подпись, если включены); под активным полоска акцента.</summary>
    private void SetTaskButtonContent(Button b, TaskWindow w)
    {
        double isz = _s.Icon;
        var g = new Grid { MinWidth = isz + 12, Height = _s.Button - 10 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = _s.Labels && !_vert ? HorizontalAlignment.Left : HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (w.Icon != null)
            row.Children.Add(new Image { Source = w.Icon, Width = isz, Height = isz, VerticalAlignment = VerticalAlignment.Center });
        else
            row.Children.Add(new TextBlock { Text = w.Title.Length > 0 ? w.Title[..1].ToUpperInvariant() : "?", Foreground = _accent, FontWeight = FontWeights.SemiBold,
                FontSize = isz * .62, Width = isz, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        if (_s.Labels && !_vert)
            row.Children.Add(new TextBlock { Text = w.Title, Foreground = _fg, FontSize = 12, Margin = new Thickness(8, 0, 4, 0), MaxWidth = 130,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        g.Children.Add(row);
        var pill = new Border { Background = _fgDim, CornerRadius = new CornerRadius(1.5), Tag = "pill" };
        if (_vert)
        {
            pill.Width = 3; pill.Height = 8; pill.VerticalAlignment = VerticalAlignment.Center;
            pill.HorizontalAlignment = _s.Edge == BarSettings.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            pill.Margin = _s.Edge == BarSettings.Right ? new Thickness(0, 0, -3, 0) : new Thickness(-3, 0, 0, 0);
        }
        else
        {
            pill.Height = 3; pill.Width = 8; pill.HorizontalAlignment = HorizontalAlignment.Center;
            pill.VerticalAlignment = _s.Edge == BarSettings.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            pill.Margin = _s.Edge == BarSettings.Top ? new Thickness(0, -3, 0, 0) : new Thickness(0, 0, 0, -3);
        }
        g.Children.Add(pill);
        b.Content = g;
        b.ToolTip = null; // подсказку заменяет миниатюра
        AutomationProps(b, w.Title);
    }

    private void UpdateActive()
    {
        foreach (var (h, b) in _buttons)
        {
            bool on = h == _foreground;
            b.Background = on ? _active : Brushes.Transparent;
            if (b.Content is Grid g && g.Children.OfType<Border>().FirstOrDefault(x => (string?)x.Tag == "pill") is { } pill)
            {
                pill.Background = on ? _accent : _fgDim;
                if (_vert) pill.Height = on ? 16 : 8; else pill.Width = on ? 16 : 8;
            }
        }
    }

    /// <summary>Щелчок: активное окно — свернуть, иначе — развернуть и вывести на передний план.</summary>
    private void OnTaskClick(IntPtr hwnd)
    {
        _thumb.HidePopup();
        if (!Native.IsWindow(hwnd)) { _tracker?.QueueRefresh(); return; }

        if (hwnd == _foreground && !Native.IsIconic(hwnd))
        {
            // Просим само окно свернуться (WM_SYSCOMMAND, как кнопка «Свернуть» в его заголовке).
            Native.PostMessage(hwnd, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_MINIMIZE), IntPtr.Zero);
            _foreground = IntPtr.Zero;
        }
        else Activate(hwnd);
        UpdateActive();
    }

    private void Activate(IntPtr hwnd)
    {
        if (!Native.IsWindow(hwnd)) return;
        if (Native.IsIconic(hwnd))
            Native.PostMessage(hwnd, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_RESTORE), IntPtr.Zero);
        // Панель только что получила ввод, поэтому система разрешает ей передать передний план.
        if (Native.SetForegroundWindow(hwnd)) _foreground = hwnd;   // не ждём события — повторный щелчок сразу свернёт
        UpdateActive();
    }

    private static void Close(IntPtr hwnd)
    {
        const uint WM_CLOSE = 0x0010;
        Native.PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    private void ShowThumbForHover()
    {
        var b = _hoverButton;
        if (b?.Tag is not IntPtr hwnd || !Native.IsWindow(hwnd)) return;

        // Миниатюра: у нижней — над кнопкой, у верхней — под ней, у боковых — сбоку.
        Point at = _s.Edge switch
        {
            BarSettings.Left => new Point(b.ActualWidth + 8 + _thumb.Width / 2, b.ActualHeight / 2),
            BarSettings.Right => new Point(-8 - _thumb.Width / 2, b.ActualHeight / 2),
            BarSettings.Top => new Point(b.ActualWidth / 2, b.ActualHeight + 8),
            _ => new Point(b.ActualWidth / 2, 0),
        };
        var px = b.PointToScreen(at);
        var ps = PresentationSource.FromVisual(this);
        var dip = ps?.CompositionTarget?.TransformFromDevice.Transform(px) ?? px;
        if (_vert) dip.Y += _thumb.Height / 2 + 6;
        if (_s.Edge == BarSettings.Top) dip.Y += _thumb.Height + 6;
        _thumb.ShowFor(hwnd, Native.GetTitle(hwnd), dip);
    }

    // ---------------- Пуск и «^» ----------------

    private void OnStart()
    {
        var ven4Start = System.IO.Path.Combine(AppContext.BaseDirectory, "Ven4Start.exe");
        if (System.IO.File.Exists(ven4Start))
        {
            try
            {
                // где стоит наш «Пуск» (пиксели экрана) — «Свой Пуск» откроется ровно над ним (у боковой панели — рядом сверху)
                var pt = _startButton != null ? _startButton.PointToScreen(new Point(_startButton.ActualWidth / 2, _startButton.ActualHeight / 2)) : new Point(0, 0);
                Process.Start(new ProcessStartInfo(ven4Start, $"--show --anchor {(int)pt.X},{(int)pt.Y}{(_s.Edge == BarSettings.Left ? ",left" : "")}") { UseShellExecute = false });
                return;
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
            }
        }
        // Родной «Пуск»: Ctrl+Esc — документированное сочетание клавиш Windows.
        Native.SendChord(Native.VK_CONTROL, Native.VK_ESCAPE);
    }

    /// <summary>
    /// Значки трея сторонних программ нам недоступны документированным путём, поэтому «^»
    /// показывает родную панель: при --hide-native временно снимаем автоскрытие (ABM_SETSTATE),
    /// повторный щелчок — прячем обратно. Без --hide-native родная панель и так видна —
    /// переводим фокус в её область уведомлений сочетанием Win+B.
    /// </summary>
    private void OnOverflow()
    {
        if (NativeTaskbar.IsAutoHideForcedByUs)
        {
            if (_nativeShownTemporarily) NativeTaskbar.HideAgain();
            else NativeTaskbar.TemporarilyShow();
            _nativeShownTemporarily = !_nativeShownTemporarily;
        }
        else
        {
            Native.SendChord(Native.VK_LWIN, Native.VK_B);
        }
    }

    // ---------------- Выход ----------------

    private bool _cleaned;

    public void Cleanup()
    {
        if (_cleaned) return;
        _cleaned = true;
        _clockTimer.Stop();
        _hoverTimer.Stop();
        _hideTimer.Stop();
        _dragTimer.Stop();
        try { _thumb.Close(); } catch { }
        _tracker?.Dispose();
        _volume.Dispose();
        _appBar?.Unregister();
        NativeTaskbar.Restore();
    }

    /// <summary>Аварийная уборка из любого потока: только то, что безопасно вне UI-потока.</summary>
    public void EmergencyCleanup()
    {
        _appBar?.Unregister();
        NativeTaskbar.Restore();
    }
}
