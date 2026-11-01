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

    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Start", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _single = new Mutex(true, @"Local\Ven4Start.Single", out bool first);
        if (!first) { Shutdown(); return; }

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
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Свой Пуск — Ctrl+Alt+Пробел",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, a) => { if (a.Button == Forms.MouseButtons.Left) ShowMenu(); };
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
