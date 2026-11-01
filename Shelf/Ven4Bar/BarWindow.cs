using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Ven4Bar;

/// <summary>
/// Сама панель: «Пуск» слева, кнопки окон, справа «^», язык, громкость, часы.
/// </summary>
internal sealed class BarWindow : Window
{
    public const double BarHeightDip = 40;
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    private static readonly Brush BarBg = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F));
    private static readonly Brush ButtonBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30));
    private static readonly Brush ActiveBg = new SolidColorBrush(Color.FromRgb(0x3A, 0x4A, 0x6A));
    private static readonly Brush Fg = Brushes.White;

    private readonly bool _hideNative;
    private AppBar? _appBar;
    private WindowTracker? _tracker;
    private readonly Volume _volume = new();
    private readonly ThumbnailPopup _thumb = new();
    private readonly DispatcherTimer _clockTimer = new();
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private readonly StackPanel _tasksPanel = new() { Orientation = Orientation.Horizontal };
    private readonly Dictionary<IntPtr, Button> _buttons = new();
    private readonly TextBlock _clockTime = new() { Foreground = Fg, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _clockDate = new() { Foreground = Fg, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _volumeIcon = new() { Foreground = Fg, FontFamily = new FontFamily(IconFont), FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _langText = new() { Foreground = Fg, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private Button? _hoverButton;
    private IntPtr _foreground;
    private bool _nativeShownTemporarily;

    public event Action? ExitRequested;

    public BarWindow(bool hideNative)
    {
        _hideNative = hideNative;
        Title = "Ven4Bar";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = BarBg;
        Height = BarHeightDip;
        Width = SystemParameters.PrimaryScreenWidth;
        Left = 0;
        Top = SystemParameters.PrimaryScreenHeight - BarHeightDip;

        Content = BuildLayout();
        ContextMenu = BuildContextMenu();

        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => Cleanup();

        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); ShowThumbForHover(); };
    }

    // ---------------- Разметка ----------------

    private UIElement BuildLayout()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var start = MakeButton(new TextBlock
        {
            Text = "", // значок «Пуск» из шрифта значков Windows
            FontFamily = new FontFamily(IconFont),
            FontSize = 18,
            Foreground = Fg,
        }, "Пуск");
        start.Width = 48;
        start.Click += (_, _) => OnStart();
        Grid.SetColumn(start, 0);
        grid.Children.Add(start);

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _tasksPanel,
        };
        scroll.PreviewMouseWheel += (_, e) =>
        {
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta / 2.0);
            e.Handled = true;
        };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);

        var right = new StackPanel { Orientation = Orientation.Horizontal };

        var overflow = MakeButton(new TextBlock { Text = "", FontFamily = new FontFamily(IconFont), FontSize = 12, Foreground = Fg },
            "Показать скрытые значки (родная панель задач)");
        overflow.Width = 32;
        overflow.Click += (_, _) => OnOverflow();
        right.Children.Add(overflow);

        var lang = MakeButton(_langText, "Язык ввода");
        lang.Width = 40;
        lang.Click += (_, _) => UpdateLanguage(Native.GetForegroundWindow());
        right.Children.Add(lang);

        var vol = MakeButton(_volumeIcon, "Громкость: колесо мыши — тише/громче, щелчок — выключить звук");
        vol.Width = 40;
        vol.Click += (_, _) => { if (_volume.Available) _volume.Muted = !_volume.Muted; UpdateVolumeIcon(); };
        vol.MouseWheel += (_, e) =>
        {
            if (!_volume.Available) return;
            _volume.Level += e.Delta > 0 ? 0.02f : -0.02f;
            UpdateVolumeIcon();
            e.Handled = true;
        };
        right.Children.Add(vol);

        var clockStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        clockStack.Children.Add(_clockTime);
        clockStack.Children.Add(_clockDate);
        var clock = MakeButton(clockStack, "Дата и время");
        clock.Width = 84;
        right.Children.Add(clock);

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        var exit = new MenuItem { Header = "Выход" };
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exit);
        return menu;
    }

    private static Button MakeButton(object content, string tip)
    {
        return new Button
        {
            Content = content,
            ToolTip = tip,
            Background = Brushes.Transparent,
            Foreground = Fg,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(2, 3, 2, 3),
            Padding = new Thickness(6, 0, 6, 0),
            Focusable = false,
            Template = FlatTemplate(),
        };
    }

    /// <summary>Плоский шаблон кнопки: фон из Background, подсветка при наведении.</summary>
    private static ControlTemplate FlatTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding")
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        t.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), "bd"));
        t.Triggers.Add(hover);
        return t;
    }

    // ---------------- Запуск ----------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // Своё окно — инструментальное: не попадает в Alt+Tab и в свой же список.
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));

        var src = HwndSource.FromHwnd(hwnd);
        _appBar = new AppBar(hwnd, DipToPx(BarHeightDip));
        src.AddHook(_appBar.WndProc);
        src.AddHook(OwnWndProc);
        _appBar.FullscreenChanged += fs => { Topmost = !fs; if (fs) _thumb.HidePopup(); };
        _appBar.Register();

        if (_hideNative) NativeTaskbar.EnableAutoHide();

        _tracker = new WindowTracker(Dispatcher);
        _tracker.WindowsChanged += OnWindowsChanged;
        _tracker.ForegroundChanged += h => Dispatcher.BeginInvoke(() =>
        {
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
            Dispatcher.BeginInvoke(() => _appBar?.SetHeight(DipToPx(BarHeightDip)));
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
        b.MaxWidth = 180;
        b.MinWidth = 48;
        b.HorizontalContentAlignment = HorizontalAlignment.Left;
        b.Click += (_, _) => OnTaskClick(hwnd);
        b.MouseEnter += (_, _) => { _hoverButton = b; _hoverTimer.Stop(); _hoverTimer.Start(); };
        b.MouseLeave += (_, _) => { if (_hoverButton == b) _hoverButton = null; _hoverTimer.Stop(); _thumb.HidePopup(); };
        b.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) Close(hwnd); };
        return b;
    }

    private static void SetTaskButtonContent(Button b, TaskWindow w)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        if (w.Icon != null)
            sp.Children.Add(new Image { Source = w.Icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
        sp.Children.Add(new TextBlock
        {
            Text = w.Title,
            Foreground = Fg,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 140,
            VerticalAlignment = VerticalAlignment.Center,
        });
        b.Content = sp;
        b.ToolTip = null; // подсказку заменяет миниатюра
    }

    private void UpdateActive()
    {
        foreach (var (h, b) in _buttons)
            b.Background = h == _foreground ? ActiveBg : ButtonBg;
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
        else
        {
            if (Native.IsIconic(hwnd))
                Native.PostMessage(hwnd, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_RESTORE), IntPtr.Zero);
            // Панель только что получила щелчок, поэтому система разрешает ей передать передний план.
            Native.SetForegroundWindow(hwnd);
        }
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

        // Координаты верхней середины кнопки: в пиксели экрана, затем обратно в DIP.
        var px = b.PointToScreen(new Point(b.ActualWidth / 2, 0));
        var ps = PresentationSource.FromVisual(this);
        var dip = ps?.CompositionTarget?.TransformFromDevice.Transform(px) ?? px;
        _thumb.ShowFor(hwnd, Native.GetTitle(hwnd), dip);
    }

    // ---------------- Пуск и «^» ----------------

    private void OnStart()
    {
        var ven4Start = Path.Combine(AppContext.BaseDirectory, "Ven4Start.exe");
        if (File.Exists(ven4Start))
        {
            try
            {
                Process.Start(new ProcessStartInfo(ven4Start, "--show") { UseShellExecute = false });
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
