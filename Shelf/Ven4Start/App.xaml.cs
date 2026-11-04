using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Ven4Start;

public partial class App : Application
{
    private static Mutex? _single;
    private HotkeyService? _hotkey;
    private WinKeyHook? _winHook;
    private Forms.NotifyIcon? _tray;
    private MenuWindow? _menu;
    private EventWaitHandle? _show;
    private const string ShowEvent = @"Local\Ven4Start.Show";

    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Start", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool showNow = e.Args.Any(a => string.Equals(a, "--show", StringComparison.OrdinalIgnoreCase));
        // --anchor X,Y[,left] — где стоит «Пуск» своей панели Ven4Bar: меню откроется у него.
        var ai = Array.FindIndex(e.Args, a => string.Equals(a, "--anchor", StringComparison.OrdinalIgnoreCase));
        if (ai >= 0 && ai + 1 < e.Args.Length) MenuWindow.SaveAnchor(e.Args[ai + 1]);
        _single = new Mutex(true, @"Local\Ven4Start.Single", out bool first);
        if (!first)
        {
            // Уже запущен — попросить его открыть меню (так клиент Ven4Tools показывает «Свой Пуск»).
            if (showNow && EventWaitHandle.TryOpenExisting(ShowEvent, out var ev)) using (ev) ev.Set();
            Shutdown(); return;
        }
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
        new Thread(() => { while (_show.WaitOne()) Dispatcher.BeginInvoke(new Action(ShowMenu)); }) { IsBackground = true, Name = "Ven4Start.Show" }.Start();

        // «Сторож»: родной Пуск мы не трогаем вообще, поэтому падение ничего не ломает.
        // Хук клавиатуры система снимает сама при завершении процесса; здесь — только журнал
        // и аккуратное снятие хука, если ошибка поймана.
        DispatcherUnhandledException += (_, a) => { Log(a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => { Log(a.ExceptionObject as Exception); _winHook?.Dispose(); };

        _menu = new MenuWindow();

        _hotkey = new HotkeyService(ToggleMenu);

        bool winKey = e.Args.Any(a => string.Equals(a, "--win-key", StringComparison.OrdinalIgnoreCase));
        if (winKey)
            _winHook = new WinKeyHook(() => Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ShowMenu)));

        CreateTray();
        if (showNow) Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ShowMenu));

        if (!_hotkey.Registered)
            _tray!.ShowBalloonTip(5000, "Свой Пуск",
                "Не удалось занять Ctrl+Alt+Пробел — сочетание уже используется. Меню открывается из значка в трее.",
                Forms.ToolTipIcon.Warning);
        if (winKey && _winHook is { Installed: false })
            _tray!.ShowBalloonTip(5000, "Свой Пуск", "Не удалось включить открытие по клавише Win.", Forms.ToolTipIcon.Warning);
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть меню", null, (_, _) => ShowMenu());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());

        _tray = new Forms.NotifyIcon
        {
            Icon = TrayIcon(),
            Text = "Свой Пуск — Ctrl+Alt+Пробел",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, a) => { if (a.Button == Forms.MouseButtons.Left) ShowMenu(); };
    }

    /// <summary>Значок в трее — оригинальная морда Венчика; если картинка не загрузилась — нарисованная голова.</summary>
    private static System.Drawing.Icon TrayIcon()
    {
        try
        {
            var res = GetResourceStream(new Uri("pack://application:,,,/Ven4Start;component/Resources/venchik-face.png"));
            if (res != null)
            {
                using var src = new System.Drawing.Bitmap(res.Stream);
                var n = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);   // 16 / 20 / 24 — по масштабу экрана
                using var bmp = new System.Drawing.Bitmap(n, n);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.Clear(System.Drawing.Color.Transparent);
                    g.DrawImage(src, 0, 0, n, n);
                }
                return System.Drawing.Icon.FromHandle(bmp.GetHicon());
            }
        }
        catch (Exception ex) { Log(ex); }
        return DrawnTrayIcon();
    }

    private static System.Drawing.Icon DrawnTrayIcon()
    {
        var t = StartTheme.Current();
        var acc = StartTheme.C(t.Accent); var bar = StartTheme.C(t.Bar);
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var head = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(acc.R, acc.G, acc.B));
            using var face = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(bar.R, bar.G, bar.B));
            g.FillPolygon(head, new[] { new System.Drawing.PointF(3, 15), new System.Drawing.PointF(7, 2), new System.Drawing.PointF(14, 10) });
            g.FillPolygon(head, new[] { new System.Drawing.PointF(29, 15), new System.Drawing.PointF(25, 2), new System.Drawing.PointF(18, 10) });
            g.FillEllipse(head, 3, 7, 26, 23);
            g.FillEllipse(face, 9, 15, 4, 4); g.FillEllipse(face, 19, 15, 4, 4);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private void ToggleMenu()
    {
        if (_menu == null) return;
        if (_menu.IsVisible) _menu.HideMenu();
        else ShowMenu();
    }

    private void ShowMenu() => _menu?.ShowMenu();

    private void ExitApp()
    {
        _winHook?.Dispose();
        _hotkey?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _menu?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _winHook?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }

    private static void Log(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}{Environment.NewLine}");
        }
        catch { }
    }
}
