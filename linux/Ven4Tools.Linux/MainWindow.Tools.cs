using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Ven4Tools.Linux.Services;

namespace Ven4Tools.Linux;

// Вкладки, перенесённые из клиента Windows 7.0: «Персонализация», «Система» с очисткой места, «Автозагрузка».
public partial class MainWindow
{
    private List<CleanupItem> _cleanup = new();

    private void InitToolTabs()
    {
        LookTab.Header = L.T("Персонализация");
        SystemTab.Header = L.T("Система");
        AutostartTab.Header = L.T("Автозагрузка");
        LookIntro.Text = L.T("Оформление рабочего стола под Венчика. Всё возвращается как было одной кнопкой.");
        WallpaperTitle.Text = L.T("Обои");
        LockTitle.Text = L.T("Экран блокировки");
        WallpaperApply.Content = LockApply.Content = L.T("Применить");
        WallpaperRevert.Content = LockRevert.Content = L.T("Вернуть как было");
        CleanupLabel.Text = L.T("Место на диске");
        CleanupScanButton.Content = L.T("Проверить");
        CleanupRunButton.Content = L.T("Очистить отмеченное");
        AutostartIntro.Text = L.T("Программы, которые запускаются при входе в систему. Выключенная программа остаётся установленной.");

        WallpaperPreview.Source = LoadPreview("venchik/wallpaper.jpg");
        LockPreview.Source = LoadPreview("venchik/lockscreen.jpg");
        UpdateLookState();
        ShowSystemInfo();
        var autostart = Autostart.List();
        AutostartList.ItemsSource = autostart;
        if (autostart.Count == 0)
            AutostartIntro.Text += "\n\n" + L.T("Сейчас при входе не запускается ни одна программа, кроме служб самой системы.");
    }

    // ---------- Персонализация ----------

    private static Bitmap? LoadPreview(string resource)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        return s is null ? null : Bitmap.DecodeToWidth(s, 760);
    }

    private void UpdateLookState()
    {
        var supported = Personalization.IsSupported;
        foreach (var (part, state, apply, revert) in new[]
                 {
                     (LookPart.Wallpaper, WallpaperState, WallpaperApply, WallpaperRevert),
                     (LookPart.LockScreen, LockState, LockApply, LockRevert),
                 })
        {
            var applied = Personalization.HasUndo(part);
            apply.IsEnabled = supported && !_busy;
            revert.IsEnabled = applied && !_busy;
            state.Text = !supported ? L.T("Этот рабочий стол пока не поддерживается.")
                : applied ? L.T("Применено. Прежнее оформление сохранено.")
                : L.T("Сейчас стоит ваше оформление.");
        }
        // Проверено на Ubuntu 26.04: GNOME показывает на экране блокировки размытые обои рабочего стола,
        // отдельная картинка ему не задаётся. Кнопку не прячем - на Cinnamon и MATE она работает
        if (Personalization.IsGnome || Personalization.IsKde)
        {
            LockApply.IsEnabled = false;
            LockState.Text = L.T("Здесь экран блокировки показывает обои рабочего стола: поставьте обои с Венчиком.");
        }
    }

    private async void OnLookApply(object? sender, RoutedEventArgs e) => await RunLookAsync(sender, revert: false);

    private async void OnLookRevert(object? sender, RoutedEventArgs e) => await RunLookAsync(sender, revert: true);

    private async Task RunLookAsync(object? sender, bool revert)
    {
        if (_busy || sender is not Button { Tag: string tag } || !Enum.TryParse<LookPart>(tag, out var part)) return;
        _busy = true;
        try
        {
            var ok = revert ? await Personalization.RevertAsync(part, Log) : await Personalization.ApplyAsync(part, Log);
            Log(L.T(ok ? "Готово." : "Не получилось, подробности выше."));
        }
        catch (Exception ex) { Log(L.T("Ошибка: ") + ex.Message); }
        finally
        {
            _busy = false;
            UpdateLookState();
        }
    }

    // ---------- Система ----------

    private void ShowSystemInfo()
    {
        var (total, available) = SystemInfo.Memory();
        var up = SystemInfo.Uptime();
        var lines = new List<string>
        {
            $"{L.T("Система")}:      {Distro.Name}",
            $"{L.T("Ядро")}:         {SystemInfo.Kernel()}",
            $"{L.T("Рабочий стол")}: {SystemInfo.Desktop()}",
            $"{L.T("Процессор")}:    {SystemInfo.Cpu()}",
            $"{L.T("Память")}:       {SystemInfo.Size(total)}, {L.T("свободно")} {SystemInfo.Size(available)}",
            $"{L.T("Работает")}:     {(int)up.TotalDays} {L.T("д")} {up.Hours} {L.T("ч")} {up.Minutes} {L.T("мин")}",
        };
        foreach (var d in SystemInfo.Disks())
            lines.Add($"{L.T("Диск")} {d.Mount} ({d.Format}): {SystemInfo.Size(d.Total)}, {L.T("свободно")} {SystemInfo.Size(d.Free)}");
        SystemInfoText.Text = string.Join('\n', lines);
    }

    private async void OnCleanupScan(object? sender, RoutedEventArgs e)
    {
        CleanupScanButton.IsEnabled = false;
        try
        {
            _cleanup = await Cleanup.ScanAsync();
            CleanupList.ItemsSource = _cleanup;
            CleanupRunButton.IsEnabled = _cleanup.Count > 0;
        }
        finally { CleanupScanButton.IsEnabled = true; }
    }

    private async void OnCleanupRun(object? sender, RoutedEventArgs e)
    {
        var selected = _cleanup.Where(i => i.IsSelected).ToList();
        if (_busy || selected.Count == 0) return;
        _busy = true;
        CleanupRunButton.IsEnabled = false;
        try
        {
            var ok = await Cleanup.RunAsync(selected, Log);
            Log(L.T(ok ? "Готово." : "Не получилось, подробности выше."));
        }
        catch (Exception ex) { Log(L.T("Ошибка: ") + ex.Message); }
        finally { _busy = false; }
        ShowSystemInfo();
        OnCleanupScan(sender, e);
    }

    // ---------- Автозагрузка ----------

    private void OnAutostartToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { DataContext: AutostartEntry entry } toggle) return;
        var enable = toggle.IsChecked == true;
        try
        {
            Autostart.SetEnabled(entry, enable);
            Log($"{entry.Name}: {L.T(enable ? "запуск при входе включён" : "запуск при входе выключен")}");
        }
        catch (Exception ex)
        {
            toggle.IsChecked = !enable;
            Log(L.T("Ошибка: ") + ex.Message);
        }
    }
}
