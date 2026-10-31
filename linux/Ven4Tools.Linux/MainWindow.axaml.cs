using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Ven4Tools.Linux.Models;
using Ven4Tools.Linux.Services;
using Ven4Tools.Services;

namespace Ven4Tools.Linux;

public partial class MainWindow : Window
{
    // Порядок имён как в клиенте Windows: латиница, затем кириллица. Правила ICU для
    // ru-RU ставят кириллицу первой, поэтому сравнение нейтральное
    private static readonly StringComparer NameOrder = StringComparer.InvariantCultureIgnoreCase;

    // Английские подписи готовых наборов: в ReadySets они только на русском
    private static readonly Dictionary<string, string> SetDescriptionsEn = new()
    {
        ["home"] = "Browser, messenger, video, archiver, documents and PDF.",
        ["work"] = "Browser, office, PDF, video calls, messenger, screenshots and remote access.",
        ["games"] = "Game stores, voice chat, recording and GPU monitoring.",
        ["dev"] = "Code editor, Git, Python, Node.js, Postman and a text editor.",
    };

    private readonly string _allCategories = L.T("Все категории");
    private List<AppItem> _items = new();
    private LinuxPackageMap _map = new();
    private string _catalogText = "";
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        ApplyTexts();
        SearchBox.TextChanged += (_, _) => ApplyFilter();
        CategoryBox.SelectionChanged += (_, _) => ApplyFilter();
        LinuxOnlyBox.IsCheckedChanged += (_, _) => ApplyFilter();
        FavoritesOnlyBox.IsCheckedChanged += (_, _) => ApplyFilter();
        LanguageBox.SelectionChanged += OnLanguageChanged;
        Opened += async (_, _) => await LoadAsync();
    }

    private void ApplyTexts()
    {
        Title = TitleText.Text = L.T("Ven4Tools для Linux");
        StatusLine.Text = L.T("Загружаю каталог…");
        CatalogTab.Header = L.T("Каталог");
        SetsTab.Header = L.T("Наборы");
        UpdatesTab.Header = L.T("Обновления");
        SettingsTab.Header = L.T("Настройки");
        SearchBox.Watermark = L.T("Поиск программы");
        LinuxOnlyBox.Content = L.T("Только доступные для Linux");
        FavoritesOnlyBox.Content = L.T("Только избранное");
        FlatpakButton.Content = L.T("Включить Flathub");
        ClearSelectionButton.Content = L.T("Снять отметки");
        PresetNameBox.Watermark = L.T("Название набора");
        SavePresetButton.Content = L.T("Сохранить отмеченные как набор");
        CheckUpdatesButton.Content = L.T("Проверить обновления");
        UpdateAllButton.Content = L.T("Обновить всё");
        UpdatesText.Text = L.T("Нажмите «Проверить обновления».");
        LanguageLabel.Text = L.T("Язык интерфейса");
        LanguageHint.Text = L.T("Перезапустите программу, чтобы сменить язык.");
        ProfileLabel.Text = L.T("Профиль");
        ProfileHint.Text = L.T("Избранное и ваши наборы. Архив совместим с Ven4Tools для Windows.");
        ExportButton.Content = L.T("Экспорт профиля");
        ImportButton.Content = L.T("Импорт профиля");
        LogLabel.Text = L.T("Журнал");
        LanguageBox.ItemsSource = new[] { "Русский", "English" };
        LanguageBox.SelectedIndex = L.IsEnglish ? 1 : 0;
        UpdateSelectionButton();
    }

    // ---------- Загрузка ----------

    private async Task LoadAsync()
    {
        try
        {
            _map = CatalogLoader.LoadPackageMap();
            var (catalog, source) = await CatalogLoader.LoadAsync(Log);
            _catalogText = L.IsEnglish
                ? $"Catalog v{catalog.Version} ({source}), apps: {catalog.Apps.Count}"
                : $"Каталог v{catalog.Version} ({source}), программ: {catalog.Apps.Count}";
            BuildItems(catalog.Apps);
            await RefreshInstalledAsync();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            StatusLine.Text = L.T("Ошибка: ") + ex.Message;
            Log(ex.ToString());
        }
    }

    private void UpdateStatus()
    {
        var ready = _items.Count(i => i.HasLinuxPackage);
        StatusLine.Text = L.IsEnglish
            ? $"{Distro.Name} · {_catalogText}, installable here: {ready}. Flatpak: {(Flatpak.IsAvailable ? "yes" : "no")}, Snap: {(Snap.IsAvailable ? "yes" : "no")}."
            : $"{Distro.Name} · {_catalogText}, можно поставить в этой системе: {ready}. Flatpak: {(Flatpak.IsAvailable ? "есть" : "нет")}, Snap: {(Snap.IsAvailable ? "есть" : "нет")}.";
        FlatpakButton.IsVisible = !Flatpak.IsAvailable;
    }

    private void BuildItems(List<CatalogApp> apps)
    {
        _items = apps.Select(a =>
        {
            _map.Apps.TryGetValue(a.Id, out var pkg);
            return new AppItem
            {
                App = a, Package = pkg, Backend = PickBackend(pkg),
                IsFavorite = UserData.Favorites.Contains(a.Id)
            };
        }).ToList();

        var categories = _items.Select(i => i.Category).Where(c => c.Length > 0).Distinct().OrderBy(c => c, NameOrder).ToList();
        categories.Insert(0, _allCategories);
        CategoryBox.ItemsSource = categories;
        CategoryBox.SelectedIndex = 0;
        ApplyFilter();
        BuildSets();
    }

    // Flatpak в приоритете: ставит без пароля и одинаково на любом дистрибутиве
    private static BackendKind? PickBackend(LinuxPackage? pkg)
    {
        if (pkg is null) return null;
        if (pkg.Flatpak is not null && Flatpak.IsAvailable) return BackendKind.Flatpak;
        if (pkg.Snap is not null && Snap.IsAvailable) return BackendKind.Snap;
        return null;
    }

    private async Task RefreshInstalledAsync()
    {
        var flatpaks = await Flatpak.ListInstalledAsync();
        var snaps = await Snap.ListInstalledAsync();
        foreach (var i in _items)
        {
            i.IsInstalled = i.Backend switch
            {
                BackendKind.Flatpak => flatpaks.Contains(i.PackageId!),
                BackendKind.Snap => snaps.Contains(i.PackageId!),
                _ => false
            };
        }
    }

    // ---------- Каталог ----------

    private void ApplyFilter()
    {
        var q = SearchBox.Text?.Trim() ?? "";
        var cat = CategoryBox.SelectedItem as string;
        var linuxOnly = LinuxOnlyBox.IsChecked == true;
        var favOnly = FavoritesOnlyBox.IsChecked == true;

        AppList.ItemsSource = _items
            .Where(i => !linuxOnly || i.Package is not null)
            .Where(i => !favOnly || i.IsFavorite)
            .Where(i => cat is null || cat == _allCategories || i.Category == cat)
            .Where(i => q.Length == 0
                        || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || i.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name, NameOrder)
            .ToList();
    }

    private void OnSelectionChanged(object? sender, RoutedEventArgs e) => UpdateSelectionButton();

    private void UpdateSelectionButton()
    {
        var n = _items.Count(i => i.IsSelected);
        InstallSelectedButton.Content = L.T("Установить отмеченные") + (n > 0 ? $" ({n})" : "");
        InstallSelectedButton.IsEnabled = n > 0 && !_busy;
        SavePresetButton.IsEnabled = n > 0;
    }

    private void OnClearSelection(object? sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.IsSelected = false;
        UpdateSelectionButton();
    }

    private void OnToggleFavorite(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not AppItem item) return;
        item.IsFavorite = !item.IsFavorite;
        if (item.IsFavorite) UserData.Favorites.Add(item.Id); else UserData.Favorites.Remove(item.Id);
        UserData.SaveFavorites();
        if (FavoritesOnlyBox.IsChecked == true) ApplyFilter();
    }

    private async void OnAppAction(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not AppItem item || item.PackageId is null) return;
        await RunQueueAsync(new[] { item }, remove: item.IsInstalled);
    }

    private async void OnInstallSelected(object? sender, RoutedEventArgs e)
    {
        // Уже установленные из очереди выпадают - ставить их повторно незачем
        var queue = _items.Where(i => i.IsSelected && !i.IsInstalled).ToList();
        foreach (var i in _items) i.IsSelected = false;
        await RunQueueAsync(queue, remove: false);
    }

    private async Task RunQueueAsync(IReadOnlyCollection<AppItem> queue, bool remove)
    {
        if (queue.Count == 0 || _busy) return;
        _busy = true;
        foreach (var i in queue) i.IsBusy = true;
        UpdateSelectionButton();
        var names = string.Join(", ", queue.Select(i => i.Name));
        Log("--- " + (remove ? (L.IsEnglish ? "Removing: " : "Удаление: ") : (L.IsEnglish ? "Installing: " : "Установка: ")) + names);
        try
        {
            var ok = await InstallQueue.RunAsync(queue, remove, Log);
            Log(ok ? L.T("Готово.") : L.T("Не получилось, подробности выше."));
        }
        catch (Exception ex)
        {
            Log(L.T("Ошибка: ") + ex.Message);
        }
        finally
        {
            await RefreshInstalledAsync();
            foreach (var i in queue) i.IsBusy = false;
            _busy = false;
            UpdateSelectionButton();
        }
    }

    private async void OnSetupFlatpak(object? sender, RoutedEventArgs e)
    {
        FlatpakButton.IsEnabled = false;
        Log("--- Flatpak");
        var ok = await Flatpak.SetupAsync(Log);
        Log(ok ? L.T("Готово.") : L.T("Не получилось, подробности выше."));
        if (ok)
        {
            // Источник у строк сменился - пересобираем список целиком
            BuildItems(_items.Select(i => i.App).ToList());
            await RefreshInstalledAsync();
            UpdateStatus();
        }
        FlatpakButton.IsEnabled = true;
    }

    // ---------- Наборы ----------

    private void BuildSets()
    {
        var byId = _items.ToDictionary(i => i.Id);
        SetCard Card(string title, string description, IEnumerable<string> ids, UserPreset? preset)
        {
            var list = ids.Distinct().ToList();
            return new SetCard
            {
                Title = title,
                Description = description,
                // Аналоги (Epic и GOG -> Heroic) ставятся одним пакетом, дубли убираются
                Available = list.Where(id => byId.TryGetValue(id, out var i) && i.HasLinuxPackage)
                    .Select(id => byId[id]).DistinctBy(i => i.PackageId).ToList(),
                Missing = list.Where(id => !byId.TryGetValue(id, out var i) || i.Package?.Flatpak is null && !i.HasLinuxPackage)
                    .Select(id => byId.TryGetValue(id, out var i) ? i.Name : id).ToList(),
                FlathubOnly = list.Where(id => byId.TryGetValue(id, out var i) && !i.HasLinuxPackage && i.Package?.Flatpak is not null)
                    .Select(id => byId[id].LinuxName).ToList(),
                UserPreset = preset
            };
        }

        var cards = ReadySets.All
            .Select(s => Card(L.T(s.Title),
                L.IsEnglish && SetDescriptionsEn.TryGetValue(s.Key, out var en) ? en : s.Description,
                s.AppIds, null))
            .Concat(UserData.Presets.Select(p => Card("★ " + p.Name, p.Description, p.Apps, p)))
            .ToList();
        SetList.ItemsSource = cards;
    }

    private async void OnSetAction(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SetCard card, Tag: string action }) return;
        switch (action)
        {
            case "install":
                await RunQueueAsync(card.Available.Where(i => !i.IsInstalled).ToList(), remove: false);
                break;
            case "select":
                foreach (var i in _items) i.IsSelected = card.Available.Contains(i) && !i.IsInstalled;
                UpdateSelectionButton();
                Tabs.SelectedItem = CatalogTab;
                break;
            case "delete" when card.UserPreset is not null:
                UserData.RemovePreset(card.UserPreset);
                BuildSets();
                break;
        }
    }

    private void OnSavePreset(object? sender, RoutedEventArgs e)
    {
        var ids = _items.Where(i => i.IsSelected).Select(i => i.Id).ToList();
        if (ids.Count == 0) return;
        var name = PresetNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) name = (L.IsEnglish ? "Set " : "Набор ") + (UserData.Presets.Count + 1);
        UserData.AddPreset(name, ids);
        PresetNameBox.Text = "";
        BuildSets();
        Log((L.IsEnglish ? "Set saved: " : "Набор сохранён: ") + name);
    }

    // ---------- Обновления ----------

    private async void OnCheckUpdates(object? sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        try
        {
            var flatpaks = await Flatpak.ListUpdatesAsync();
            var snaps = await Snap.ListUpdatesAsync();
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in _items.Where(i => i.PackageId is not null)) names.TryAdd(i.PackageId!, i.Name);
            string Show(string pkg) => names.TryGetValue(pkg, out var n) ? $"{n} ({pkg})" : pkg;

            var lines = flatpaks.Select(p => "Flathub · " + Show(p)).Concat(snaps.Select(p => "Snap · " + Show(p))).ToList();
            UpdatesText.Text = lines.Count == 0
                ? L.T("Обновлений нет.")
                : (L.IsEnglish ? $"Updates available: {lines.Count}" : $"Есть обновления: {lines.Count}") +
                  Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines);
            UpdateAllButton.IsEnabled = lines.Count > 0;
            _pendingFlatpakUpdates = flatpaks.Count > 0;
            _pendingSnapUpdates = snaps.Count > 0;
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private bool _pendingFlatpakUpdates, _pendingSnapUpdates;

    private async void OnUpdateAll(object? sender, RoutedEventArgs e)
    {
        UpdateAllButton.IsEnabled = false;
        Log(L.IsEnglish ? "--- Updating" : "--- Обновление");
        var ok = true;
        if (_pendingFlatpakUpdates) ok &= await Flatpak.UpdateAllAsync(Log);
        if (_pendingSnapUpdates) ok &= await Snap.UpdateAllAsync(Log);
        Log(ok ? L.T("Готово.") : L.T("Не получилось, подробности выше."));
        OnCheckUpdates(sender, e);
    }

    // ---------- Настройки ----------

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        var lang = LanguageBox.SelectedIndex == 1 ? "en" : "ru";
        if (UserData.Settings.Language == lang || (UserData.Settings.Language is null && (lang == "en") == L.IsEnglish)) return;
        UserData.Settings.Language = lang;
        UserData.SaveSettings();
        LanguageHint.IsVisible = true;
    }

    private async void OnExportProfile(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"Ven4Tools-profile-{DateTime.Now:yyyy-MM-dd}.zip",
            FileTypeChoices = new[] { new FilePickerFileType("ZIP") { Patterns = new[] { "*.zip" } } }
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            UserData.ExportProfile(path);
            Log((L.IsEnglish ? "Profile exported: " : "Профиль сохранён: ") + path);
        }
        catch (Exception ex) { Log(L.T("Ошибка: ") + ex.Message); }
    }

    private async void OnImportProfile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("ZIP") { Patterns = new[] { "*.zip" } } }
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        try
        {
            var (favs, presets) = UserData.ImportProfile(path);
            foreach (var i in _items) i.IsFavorite = UserData.Favorites.Contains(i.Id);
            BuildSets();
            ApplyFilter();
            Log(L.IsEnglish
                ? $"Profile imported: favorites {favs}, sets {presets}."
                : $"Профиль загружен: избранное {favs}, наборы {presets}.");
        }
        catch (Exception ex) { Log(L.T("Ошибка: ") + ex.Message); }
    }

    private void Log(string line) =>
        Dispatcher.UIThread.Post(() =>
        {
            LogBox.Text += line + Environment.NewLine;
            LogBox.CaretIndex = LogBox.Text.Length;
        });
}
