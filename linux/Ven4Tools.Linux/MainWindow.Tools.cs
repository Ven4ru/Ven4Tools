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
        LookHeading.Text = L.T("Персонализация");
        LookIntro.Text = L.T("Оформление рабочего стола под Венчика. Всё возвращается как было одной кнопкой.");
        LookApplyAll.Content = L.T("Применить всё");
        LookRevertAll.Content = L.T("Вернуть всё как было");
        WallpaperTitle.Text = L.T("Обои");
        WallpaperHint.Text = L.T("Венчик ночью в компьютерном классе. Левая половина тёмная — значки на рабочем столе хорошо видны.");
        LockTitle.Text = L.T("Экран блокировки");
        LockHint.Text = L.T("Спящий Венчик на экране блокировки.");
        CursorsTitle.Text = L.T("Курсоры-лапы");
        CursorsHint.Text = L.T("Указатель — стрелка в полоску, как шерсть Венчика, на ссылках — лапка с подушечками.");
        SoundsTitle.Text = L.T("Звуки Венчика");
        SoundsHint.Text = L.T("Сведения, предупреждение, ошибка, уведомление, подключение и отключение устройства. Нажмите, чтобы послушать.");
        WallpaperApply.Content = LockApply.Content = CursorsApply.Content = SoundsApply.Content = L.T("Применить");
        WallpaperRevert.Content = LockRevert.Content = CursorsRevert.Content = SoundsRevert.Content = L.T("Вернуть как было");
        foreach (var file in Personalization.SoundFiles)
        {
            var play = new Button { Content = "▶ " + L.T(SoundTitles[file]), Margin = new Avalonia.Thickness(0, 0, 6, 6), Tag = file };
            play.Click += async (_, _) =>
            {
                // async void-лямбда: исключение отсюда (файл не записался, проигрыватель
                // не запустился) уронило бы программу.
                try { await Personalization.PlayAsync(file, Log); }
                catch (Exception ex) { Log(L.T("Ошибка: ") + ex.Message); }
            };
            SoundButtons.Children.Add(play);
        }
        CleanupLabel.Text = L.T("Место на диске");
        CleanupScanButton.Content = L.T("Проверить");
        CleanupRunButton.Content = L.T("Очистить отмеченное");
        AutostartIntro.Text = L.T("Программы, которые запускаются при входе в систему. Выключенная программа остаётся установленной.");

        WallpaperPreview.Source = LoadPreview("venchik/wallpaper.jpg");
        LockPreview.Source = LoadPreview("venchik/lockscreen.jpg");
        CursorsPreview.Source = LoadPreview("venchik/paws.png");
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

    private static readonly Dictionary<string, string> SoundTitles = new()
    {
        ["info.wav"] = "Сведения",
        ["warning.wav"] = "Предупреждение",
        ["error.wav"] = "Ошибка",
        ["notify.wav"] = "Уведомление",
        ["connect.wav"] = "Подключение",
        ["disconnect.wav"] = "Отключение",
    };

    private void UpdateLookState()
    {
        foreach (var (part, state, apply, revert) in new[]
                 {
                     (LookPart.Wallpaper, WallpaperState, WallpaperApply, WallpaperRevert),
                     (LookPart.LockScreen, LockState, LockApply, LockRevert),
                     (LookPart.Cursors, CursorsState, CursorsApply, CursorsRevert),
                     (LookPart.Sounds, SoundsState, SoundsApply, SoundsRevert),
                 })
        {
            var supported = Personalization.IsSupported(part);
            var applied = Personalization.HasUndo(part);
            apply.IsEnabled = supported && !Busy;
            revert.IsEnabled = applied && !Busy;
            state.Text = !supported ? L.T("Этот рабочий стол пока не поддерживается.")
                : applied ? L.T("Применено. Прежнее оформление сохранено.")
                : L.T("Сейчас стоит ваше оформление.");
        }
        LookApplyAll.IsEnabled = !Busy && LookParts.Any(p => Personalization.IsSupported(p) && !Personalization.HasUndo(p));
        LookRevertAll.IsEnabled = !Busy && LookParts.Any(Personalization.HasUndo);
        // Экран блокировки в Linux показывает обои рабочего стола (см. Personalization.IsSupported)
        LockState.Text = L.T("Здесь экран блокировки показывает обои рабочего стола: поставьте обои с Венчиком.");
        // На X11 (Cinnamon, MATE) курсор панели и рабочего стола меняется только с новым сеансом
        if (Personalization.HasUndo(LookPart.Cursors))
            CursorsState.Text += " " + L.T(Personalization.IsX11
                ? "Полностью курсор сменится после выхода из системы и повторного входа."
                : "В уже открытых программах курсор сменится после их перезапуска.");
    }

    private static readonly LookPart[] LookParts = { LookPart.Cursors, LookPart.Wallpaper, LookPart.LockScreen, LookPart.Sounds };

    // «Применить всё»: только то, что этот рабочий стол поддерживает и что ещё не применено
    private async void OnLookApplyAll(object? sender, RoutedEventArgs e) =>
        await RunLookAllAsync(LookParts.Where(p => Personalization.IsSupported(p) && !Personalization.HasUndo(p)), revert: false);

    private async void OnLookRevertAll(object? sender, RoutedEventArgs e) =>
        await RunLookAllAsync(LookParts.Where(Personalization.HasUndo), revert: true);

    private async Task RunLookAllAsync(IEnumerable<LookPart> parts, bool revert)
    {
        if (Busy) return;
        Busy = true;
        UpdateLookState();
        var ok = true;
        try
        {
            foreach (var part in parts.ToList())
                ok &= revert ? await Personalization.RevertAsync(part, Log) : await Personalization.ApplyAsync(part, Log);
            Log(L.T(ok ? "Готово." : "Не получилось, подробности выше."));
        }
        catch (Exception ex) { Log(L.T("Ошибка: ") + ex.Message); }
        finally
        {
            Busy = false;
            UpdateLookState();
        }
    }

    private async void OnLookApply(object? sender, RoutedEventArgs e) => await RunLookAsync(sender, revert: false);

    private async void OnLookRevert(object? sender, RoutedEventArgs e) => await RunLookAsync(sender, revert: true);

    private async Task RunLookAsync(object? sender, bool revert)
    {
        if (Busy || sender is not Button { Tag: string tag } || !Enum.TryParse<LookPart>(tag, out var part)) return;
        Busy = true;
        try
        {
            var ok = revert ? await Personalization.RevertAsync(part, Log) : await Personalization.ApplyAsync(part, Log);
            Log(L.T(ok ? "Готово." : "Не получилось, подробности выше."));
        }
        catch (Exception ex) { Log(L.T("Ошибка: ") + ex.Message); }
        finally
        {
            Busy = false;
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
            // Подсчёт размера корзины и эскизов — обход файлов, в фоне: иначе окно замирает.
            _cleanup = await Task.Run(Cleanup.ScanAsync);
            CleanupList.ItemsSource = _cleanup;
            CleanupRunButton.IsEnabled = _cleanup.Count > 0;
        }
        catch (Exception ex) { Log(L.T("Ошибка: ") + ex.Message); }
        finally { CleanupScanButton.IsEnabled = true; }
    }

    private async void OnCleanupRun(object? sender, RoutedEventArgs e)
    {
        var selected = _cleanup.Where(i => i.IsSelected).ToList();
        if (Busy || selected.Count == 0) return;
        Busy = true;
        CleanupRunButton.IsEnabled = false;
        try
        {
            var ok = await Cleanup.RunAsync(selected, Log);
            Log(L.T(ok ? "Готово." : "Не получилось, подробности выше."));
        }
        catch (Exception ex) { Log(L.T("Ошибка: ") + ex.Message); }
        finally { Busy = false; }
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
